using System.Text.Json;
using FlexyParse.Nemotron;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FlexyParse.Tests;

public sealed class WebGpuNativeSmokeTests
{
    [WebGpuFact]
    public void BundledPluginExecutesReluOnGpuAndSupportsRepeatedSessionCreation()
    {
        // ONNX IR 8, opset 13: one float32 Relu, X/Y shape [4]. No weights or network access.
        var model = Convert.FromBase64String(
            "CAg6PQoMCgFYEgFZIgRSZWx1Egt3ZWJncHVfcmVsdVoPCgFYEgoKCAgBEgQKAggEYg8KAVkSCgoICAESBAoCCARCAhAN");
        var directory = Path.Combine(Path.GetTempPath(), "flexyparse-webgpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var iteration = 0; iteration < 2; iteration++)
            {
                using var settings = new SessionOptions
                {
                    EnableProfiling = true,
                    ProfileOutputPathPrefix = Path.Combine(directory, $"run-{iteration}")
                };
                NemotronSession.ConfigureExecutionProvider(settings,
                    new NemotronOptions { ExecutionMode = NemotronExecutionMode.WebGpuFp16 });
                using var session = new InferenceSession(model, settings);
                using var output = session.Run([
                    NamedOnnxValue.CreateFromTensor("X", new DenseTensor<float>(new[] { -2f, 0f, 1f, 3f }, [4]))
                ]);
                Assert.Equal(new[] { 0f, 0f, 1f, 3f }, output.Single().AsTensor<float>().ToArray());

                // A correct numeric result alone could come from CPU fallback. Check actual node placement.
                using var profile = JsonDocument.Parse(File.ReadAllText(session.EndProfiling()));
                Assert.Contains(profile.RootElement.EnumerateArray(), entry =>
                    entry.TryGetProperty("args", out var args)
                    && args.TryGetProperty("provider", out var provider)
                    && provider.GetString() == WebGpuEp.GetEpName());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class WebGpuFactAttribute : FactAttribute
{
    public WebGpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLEXYPARSE_TEST_WEBGPU") != "1")
            Skip = "Set FLEXYPARSE_TEST_WEBGPU=1 to run native WebGPU inference on device 0; no model download is required.";
    }
}
