using System.Text;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FlexyParse.Nemotron;

/// <summary>Port of the pack's navit_pos.py: antialiased interpolation, then content-normalized sampling.</summary>
internal sealed class NavitPositions
{
    private const int Grid = 128;
    private const int Width = 1152;
    private readonly float[] _positions;

    public NavitPositions(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (!reader.ReadBytes(6).SequenceEqual(new byte[] { 0x93, 78, 85, 77, 80, 89 }))
            throw new InvalidDataException("Invalid NumPy positional field.");
        var major = reader.ReadByte();
        _ = reader.ReadByte();
        var headerSize = major switch { 1 => reader.ReadUInt16(), 2 => checked((int)reader.ReadUInt32()),
            _ => throw new InvalidDataException("Unsupported NumPy format.") };
        if (headerSize > 65536) throw new InvalidDataException("Invalid NumPy header size.");
        var header = Encoding.ASCII.GetString(reader.ReadBytes(headerSize));
        var elementSize = header.Contains("'<f2'", StringComparison.Ordinal) ? 2
            : header.Contains("'<f4'", StringComparison.Ordinal) ? 4 : 0;
        if (elementSize == 0 || !header.Contains("False", StringComparison.Ordinal)
            || !header.Contains("(1, 16384, 1152)", StringComparison.Ordinal)
            || reader.BaseStream.Length - reader.BaseStream.Position != (long)Grid * Grid * Width * elementSize)
            throw new InvalidDataException("Expected little-endian FP16/FP32 C-order positional field [1, 16384, 1152].");
        _positions = new float[Grid * Grid * Width];
        for (var i = 0; i < _positions.Length; i++)
            _positions[i] = elementSize == 2 ? (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()) : reader.ReadSingle();
    }

    // Injection of a constant/synthetic field lets interpolation tests run without model downloads.
    internal NavitPositions(float[] positions) => _positions = positions;

    internal DenseTensor<float> Add(Tensor<float> patches, NemotronImage image, CancellationToken token)
    {
        var height = (image.ContentHeight + 15) / 16;
        var width = (image.ContentWidth + 15) / 16;
        var pooling = Math.Min(height, width) == 1;
        var horizontal = new float[Grid * width * Width];
        var wx = Weights(width, pooling);
        for (var row = 0; row < Grid; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var col = 0; col < width; col++)
            foreach (var (index, weight) in wx[col])
            for (var channel = 0; channel < Width; channel++)
                horizontal[(row * width + col) * Width + channel] += _positions[(row * Grid + index) * Width + channel] * weight;
        }

        var resized = new float[height * width * Width];
        var wy = Weights(height, pooling);
        for (var row = 0; row < height; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var col = 0; col < width; col++)
            foreach (var (index, weight) in wy[row])
            for (var channel = 0; channel < Width; channel++)
                resized[(row * width + col) * Width + channel] += horizontal[(index * width + col) * Width + channel] * weight;
        }

        var result = new DenseTensor<float>([1, image.Rows, image.Columns, Width]);
        var output = result.Buffer.Span;
        for (var row = 0; row < image.Rows; row++)
        {
            token.ThrowIfCancellationRequested();
            var sy = SampleCoordinate(row, image.ContentHeight, height);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, height - 1);
            var fy = sy - y0;
            for (var col = 0; col < image.Columns; col++)
            {
                var sx = SampleCoordinate(col, image.ContentWidth, width);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, width - 1);
                var fx = sx - x0;
                for (var c = 0; c < Width; c++)
                {
                    var top = Lerp(resized[(y0 * width + x0) * Width + c], resized[(y0 * width + x1) * Width + c], fx);
                    var bottom = Lerp(resized[(y1 * width + x0) * Width + c], resized[(y1 * width + x1) * Width + c], fx);
                    output[(row * image.Columns + col) * Width + c] = patches[row * image.Columns + col, c] + Lerp(top, bottom, fy);
                }
            }
        }
        return result;
    }

    private static float SampleCoordinate(int patch, int content, int target)
    {
        var start = patch * 16f;
        var extent = Math.Clamp(content - start, 0, 16);
        return Math.Clamp(Math.Min(start + extent * 0.5f, content) / content * target - 0.5f, 0, target - 1);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static (int Index, float Weight)[][] Weights(int size, bool pooling)
    {
        var result = new (int, float)[size][];
        var scale = (float)Grid / size;
        for (var i = 0; i < size; i++)
        {
            var weights = new List<(int Index, float Weight)>();
            if (pooling)
            {
                var start = (int)Math.Floor((double)i * Grid / size);
                var end = (int)Math.Ceiling((double)(i + 1) * Grid / size);
                for (var j = start; j < end; j++) weights.Add((j, 1f / (end - start)));
            }
            else
            {
                var support = Math.Max(scale, 1);
                var center = (i + 0.5f) * scale;
                var start = Math.Max(0, (int)Math.Ceiling(center - support - 0.5f));
                var end = Math.Min(Grid - 1, (int)Math.Floor(center + support - 0.5f));
                var total = 0f;
                for (var j = start; j <= end; j++)
                {
                    var weight = Math.Max(0, 1 - Math.Abs((j + 0.5f - center) / support));
                    total += weight;
                    weights.Add((j, weight));
                }
                for (var j = 0; j < weights.Count; j++) weights[j] = (weights[j].Index, weights[j].Weight / total);
            }
            result[i] = weights.ToArray();
        }
        return result;
    }
}
