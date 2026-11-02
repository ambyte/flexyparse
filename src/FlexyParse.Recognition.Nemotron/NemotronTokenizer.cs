using System.Text;
using System.Text.Json;

namespace FlexyParse.Nemotron;

/// <summary>Decode-only byte-level BPE. The control prompt uses added-token IDs, not free-text encoding.</summary>
internal sealed class NemotronTokenizer
{
    // Recommended INT8 markdown_none_pic profile, without tokenizer-inserted BOS/EOS.
    internal static readonly long[] Prompt = [2, 0, 50005, 50007, 50001, 50009];
    private readonly Dictionary<int, string> _vocabulary = [];
    private readonly HashSet<int> _special = [];
    private readonly Dictionary<char, byte> _bytes = CreateByteDecoder();

    public NemotronTokenizer(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("model").GetProperty("type").GetString() != "BPE"
            || root.GetProperty("decoder").GetProperty("type").GetString() != "ByteLevel")
            throw new InvalidDataException("Expected Nemotron byte-level BPE tokenizer.");
        foreach (var token in root.GetProperty("model").GetProperty("vocab").EnumerateObject())
            _vocabulary.Add(token.Value.GetInt32(), token.Name);
        foreach (var token in root.GetProperty("added_tokens").EnumerateArray())
        {
            var id = token.GetProperty("id").GetInt32();
            _vocabulary[id] = token.GetProperty("content").GetString()!;
            if (token.GetProperty("special").GetBoolean()) _special.Add(id);
        }

        string[] expected = ["</s>", "<s>", "<no_bbox>", "<no_classes>", "<output_markdown>", "<predict_text_in_pic>"];
        for (var i = 0; i < Prompt.Length; i++)
            if (!_vocabulary.TryGetValue((int)Prompt[i], out var value) || value != expected[i])
                throw new InvalidDataException("Nemotron control token IDs do not match the pinned pack.");
    }

    public string Decode(IEnumerable<int> ids)
    {
        using var bytes = new MemoryStream();
        var result = new StringBuilder();
        foreach (var id in ids)
        {
            if (_special.Contains(id)) continue;
            if (!_vocabulary.TryGetValue(id, out var token))
                throw new InvalidDataException("Nemotron produced an unknown token ID.");
            // Added non-special tokens (coordinates/classes) are literal strings, not byte BPE.
            if (id >= 50000)
            {
                Flush();
                result.Append(token);
                continue;
            }

            foreach (var character in token)
            {
                if (!_bytes.TryGetValue(character, out var value))
                    throw new InvalidDataException("Invalid character in byte-level tokenizer vocabulary.");
                bytes.WriteByte(value);
            }
        }

        Flush();
        return result.ToString().Trim();

        void Flush()
        {
            result.Append(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length)));
            bytes.SetLength(0);
        }
    }

    private static Dictionary<char, byte> CreateByteDecoder()
    {
        var result = new Dictionary<char, byte>();
        var extra = 0;
        for (var value = 0; value < 256; value++)
        {
            var visible = value is >= 33 and <= 126 or >= 161 and <= 172 or >= 174 and <= 255;
            result.Add((char)(visible ? value : 256 + extra++), (byte)value);
        }
        return result;
    }
}
