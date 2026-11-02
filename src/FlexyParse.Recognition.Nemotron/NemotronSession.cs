using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FlexyParse.Nemotron;

public interface INemotronSession : IDisposable
{
    Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken);
}

public interface INemotronSessionFactory
{
    INemotronSession Create(string modelsDirectory, NemotronOptions options);
}

internal sealed class NemotronSessionFactory : INemotronSessionFactory
{
    public INemotronSession Create(string modelsDirectory, NemotronOptions options) => new NemotronSession(modelsDirectory, options);
}

internal sealed class NemotronSession : INemotronSession
{
    private readonly NemotronOptions _options;
    private readonly InferenceSession _patch;
    private readonly InferenceSession _vision;
    private readonly InferenceSession _embedding;
    private readonly InferenceSession _decoder;
    private readonly NavitPositions _positions;
    private readonly NemotronTokenizer _tokenizer;

    public NemotronSession(string directory, NemotronOptions options)
    {
        _options = options;
        var sessions = new List<InferenceSession>();
        try
        {
            using var settings = new SessionOptions
            {
                IntraOpNumThreads = options.InferenceThreads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                // Dynamic page/prefix lengths would otherwise retain very large arena allocations.
                EnableMemoryPattern = false,
                EnableCpuMemArena = false
            };
            ConfigureExecutionProvider(settings, options);
            _patch = Load("patch_embed.onnx");
            _vision = Load("vision_encoder.onnx");
            _embedding = Load("embed_tokens.onnx");
            _decoder = Load("decoder_model_merged.onnx");
            _positions = new NavitPositions(Path.Combine(directory, "pos_embed.npy"));
            _tokenizer = new NemotronTokenizer(Path.Combine(directory, "tokenizer.json"));

            InferenceSession Load(string name)
            {
                var session = new InferenceSession(Path.Combine(directory, name), settings);
                sessions.Add(session);
                return session;
            }
        }
        catch
        {
            foreach (var session in sessions) session.Dispose();
            throw;
        }
    }

    internal static void ConfigureExecutionProvider(SessionOptions settings, NemotronOptions options)
    {
        if (!options.IsValid()) throw new ArgumentException("Invalid Nemotron options.", nameof(options));
        if (options.ExecutionMode == NemotronExecutionMode.CpuInt8) return;
        NemotronWebGpuProvider.Configure(settings, options.GpuDeviceId);
    }

    public Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var run = new RunOptions();
        // ONNX Runtime terminates native kernels too, not just the managed token loop.
        using var registration = cancellationToken.Register(() => run.Terminate = true);
        try { return Recognize(imagePath, run, cancellationToken); }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }, cancellationToken);

    private string Recognize(string imagePath, RunOptions run, CancellationToken token)
    {
        var image = NemotronImageProcessor.Prepare(imagePath, _options, token);
        token.ThrowIfCancellationRequested();
        using var patchOutput = _patch.Run([NamedOnnxValue.CreateFromTensor("patches", image.Patches)], _patch.OutputNames, run);
        var positioned = _positions.Add(patchOutput.First().AsTensor<float>(), image, token);
        using var visionOutput = _vision.Run([NamedOnnxValue.CreateFromTensor("patch_tokens", positioned)], _vision.OutputNames, run);
        var vision = visionOutput.First(value => value.Name == "vision").AsTensor<float>();
        var mask = visionOutput.First(value => value.Name == "visual_mask").AsTensor<float>();
        using var promptOutput = Embed(NemotronTokenizer.Prompt, run);
        var prompt = promptOutput.First().AsTensor<float>();
        var prefixLength = vision.Dimensions[1] + prompt.Dimensions[1];
        var generationSteps = NemotronGenerationGuard.GetGenerationSteps(prefixLength);
        var prefix = new DenseTensor<float>([1, prefixLength, 1024]);
        vision.ToArray().CopyTo(prefix.Buffer.Span);
        prompt.ToArray().CopyTo(prefix.Buffer.Span[checked((int)vision.Length)..]);
        var attention = new float[NemotronGenerationGuard.ContextLength];
        mask.ToArray().CopyTo(attention, 0);
        Array.Fill(attention, 1f, checked((int)mask.Length), attention.Length - checked((int)mask.Length));

        var generated = new List<int>();
        var seen = NemotronTokenizer.Prompt.Select(id => (int)id).ToHashSet();
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? previous = null;
        try
        {
            Tensor<float> embeddings = prefix;
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? nextEmbedding = null;
            try
            {
                var pastLength = 0;
                for (var step = 0; step < generationSteps; step++)
                {
                    token.ThrowIfCancellationRequested();
                    var totalLength = pastLength + embeddings.Dimensions[1];
                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("inputs_embeds", embeddings),
                        NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<float>(attention.AsMemory(0, totalLength), [1, totalLength]))
                    };
                    for (var layer = 0; layer < 10; layer++)
                    foreach (var kind in new[] { "key", "value" })
                    {
                        var cache = previous is null ? new DenseTensor<float>([1, 8, 0, 64])
                            : previous.First(value => value.Name == $"present_{kind}_{layer}").AsTensor<float>();
                        inputs.Add(NamedOnnxValue.CreateFromTensor($"past_{kind}_{layer}", cache));
                    }

                    var output = _decoder.Run(inputs, _decoder.OutputNames, run);
                    previous?.Dispose();
                    previous = output;
                    nextEmbedding?.Dispose();
                    nextEmbedding = null;
                    var logits = output.First(value => value.Name == "logits").AsTensor<float>();
                    var selected = SelectToken(logits, seen, _options.RepetitionPenalty);
                    if (selected == 2) return _tokenizer.Decode(generated);
                    generated.Add(selected);
                    NemotronGenerationGuard.ThrowIfRepetitive(generated, _options);
                    seen.Add(selected);
                    if (step + 1 == generationSteps) break;
                    pastLength = totalLength;
                    nextEmbedding = Embed([(long)selected], run);
                    embeddings = nextEmbedding.First().AsTensor<float>();
                }

                // Never silently publish a truncated page as complete OCR.
                throw new InvalidDataException("Nemotron generation exhausted the model context before EOS; incomplete OCR was discarded. Split the image into smaller regions.");
            }
            finally { nextEmbedding?.Dispose(); }
        }
        finally { previous?.Dispose(); }
    }

    internal static int SelectToken(Tensor<float> logits, HashSet<int> seen, float repetitionPenalty)
    {
        var vocabularySize = logits.Dimensions[2];
        var last = logits.Dimensions[1] - 1;
        var best = float.NegativeInfinity;
        var selected = -1;
        for (var id = 0; id < vocabularySize; id++)
        {
            var score = logits[0, last, id];
            if (seen.Contains(id)) score = score < 0 ? score * repetitionPenalty : score / repetitionPenalty;
            if (score > best) { best = score; selected = id; }
        }
        return selected >= 0 ? selected : throw new InvalidDataException("Nemotron returned no finite token logits.");
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Embed(long[] ids, RunOptions run) =>
        _embedding.Run([NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length]))],
            _embedding.OutputNames, run);

    public void Dispose()
    {
        _decoder.Dispose();
        _embedding.Dispose();
        _vision.Dispose();
        _patch.Dispose();
    }
}
