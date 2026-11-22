using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AleappReader.Core;

/// <summary>How the scores of an ONNX classifier are turned into confidences.</summary>
public enum OnnxActivation
{
    /// <summary>Multi-label (each label on its own, 0–1): a model fine-tuned with binary cross-entropy.</summary>
    Sigmoid,
    /// <summary>Single-label (the confidences add up to 1): an ordinary classifier.</summary>
    Softmax,
    /// <summary>The model already outputs probabilities.</summary>
    None,
}

/// <summary>Preprocessing of an ONNX image classifier: the square input size (overridden by the model's own fixed input
/// size when it has one), the per-channel mean and standard deviation of 0–1 RGB (ImageNet's by default; CLIP uses
/// 0.4815/0.4578/0.4082 and 0.2686/0.2613/0.2758), and the activation.</summary>
public sealed record OnnxOptions(int InputSize = 224, float[]? Mean = null, float[]? Std = null, OnnxActivation Activation = OnnxActivation.Sigmoid)
{
    public float[] MeanOrDefault => Mean is { Length: 3 } m ? m : [0.485f, 0.456f, 0.406f];
    public float[] StdOrDefault => Std is { Length: 3 } s && s.All(x => x > 0) ? s : [0.229f, 0.224f, 0.225f];
}

/// <summary>
/// A local image classifier exported to ONNX (a fine-tuned CLIP, ViT, EfficientNet…), run with ONNX Runtime on this
/// machine: DirectML (any DirectX 12 GPU) when available, else the CPU. Nothing leaves the machine. labels.txt holds one
/// label per line, in the order of the model's outputs. The image is decoded, resized (shorter side to the input size)
/// and centre-cropped by <paramref name="decodeRgb"/> (the app's own decoder, which reads every format Windows reads),
/// then normalised here into a 1×3×H×W (or 1×H×W×3) float tensor.
/// </summary>
public sealed class OnnxCategorizer(string modelPath, string labelsPath, OnnxOptions options, Func<byte[], int, byte[]?> decodeRgb) : IImageCategorizer
{
    InferenceSession? _session;
    string? _input;
    bool _nhwc;
    int _size = options.InputSize;
    IReadOnlyList<string>? _labels;
    string? _model;
    readonly SemaphoreSlim _gate = new(1, 1);

    public string Engine => "ONNX"; // the same whether DirectML or the CPU runs it: a run resumes on either
    /// <summary>"DirectML" or "CPU", once loaded.</summary>
    public string? Provider { get; private set; }
    public string Model => _model ??= $"{Path.GetFileName(modelPath)}#{FileHash(modelPath)}";
    public string PromptVersion => "onnx-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Labels) + $"|{options.InputSize}|{string.Join(",", options.MeanOrDefault)}|{string.Join(",", options.StdOrDefault)}|{options.Activation}")))[..8];
    public bool Remote => false;
    public IReadOnlyList<string> Labels => _labels ??= ReadLabels(labelsPath);

    static string FileHash(string path)
    {
        try { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f))[..12]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "unreadable"; }
    }

    /// <summary>One label per line; blank lines and lines starting with '#' left out; "0 cat" / "0: cat" / "0,cat" read as "cat".</summary>
    public static List<string> ReadLabels(string path) =>
        [.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l != "" && !l.StartsWith('#'))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\d+\s*[:,\s]\s*", "")).Where(l => l != "")];

    /// <summary>Opens the model (throws with the reason: missing file, labels that do not match its outputs, no runtime).</summary>
    public void Load()
    {
        if (_session is not null) return;
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The ONNX model is not there.", modelPath);
        if (!File.Exists(labelsPath)) throw new FileNotFoundException("The labels file is not there.", labelsPath);
        if (Labels.Count == 0) throw new InvalidDataException("The labels file holds no labels.");
        InferenceSession? s = null;
        try
        {
            var dml = new SessionOptions();
            dml.AppendExecutionProvider_DML(0);
            s = new InferenceSession(modelPath, dml);
            Provider = "DirectML";
        }
        catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException or NotSupportedException or TypeInitializationException)
        {
            s = new InferenceSession(modelPath, new SessionOptions());
            Provider = "CPU";
        }
        var (name, meta) = s.InputMetadata.First();
        var dims = meta.Dimensions;
        if (dims.Length == 4)
        {
            _nhwc = dims[3] == 3 && dims[1] != 3;
            var h = _nhwc ? dims[1] : dims[2];
            if (h > 0) _size = h;
        }
        var outLen = s.OutputMetadata.First().Value.Dimensions.LastOrDefault();
        if (outLen > 0 && outLen != Labels.Count)
        {
            s.Dispose();
            throw new InvalidDataException($"The model gives {outLen} scores but labels.txt has {Labels.Count} labels.");
        }
        (_session, _input) = (s, name);
    }

    public async Task<CategorizationOutcome> Categorize(AiImage image, CancellationToken ct)
    {
        try
        {
            Load();
            var rgb = await Task.Run(() => decodeRgb(image.Data, _size), ct);
            if (rgb is null) return CategorizationOutcome.Failed("The image could not be decoded (no codec for this format?).");
            var tensor = Tensor(rgb, _size, options, _nhwc);
            await _gate.WaitAsync(ct);
            float[] raw;
            try
            {
                raw = await Task.Run(() =>
                {
                    using var results = _session!.Run([NamedOnnxValue.CreateFromTensor(_input!, tensor)]);
                    return results.First().AsEnumerable<float>().ToArray();
                }, ct);
            }
            finally { _gate.Release(); }
            return Outcome(raw, Labels, options.Activation);
        }
        catch (Exception e) when (e is OnnxRuntimeException or IOException or InvalidDataException or ArgumentException or InvalidOperationException
                                       or DllNotFoundException or TypeInitializationException)
        {
            return CategorizationOutcome.Failed("ONNX: " + e.Message);
        }
    }

    /// <summary>The model's scores as categories: activated, those of at least 0.01 kept, best first.</summary>
    public static CategorizationOutcome Outcome(float[] raw, IReadOnlyList<string> labels, OnnxActivation activation)
    {
        if (raw.Length != labels.Count) return CategorizationOutcome.Failed($"The model gave {raw.Length} scores for {labels.Count} labels.");
        var p = activation switch { OnnxActivation.Softmax => Softmax(raw), OnnxActivation.Sigmoid => raw.Select(Sigmoid).ToArray(), _ => raw };
        var scores = labels.Select((l, i) => new CategoryScore(l, Math.Round(Math.Clamp((double)p[i], 0, 1), 3)))
            .Where(s => s.Confidence >= 0.01).OrderByDescending(s => s.Confidence).ToList();
        return new(scores, scores.Count > 0 ? $"Top: {scores[0].Name} ({scores[0].Confidence:0.00})" : "No label above 0.01");
    }

    public static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    public static float[] Softmax(float[] x)
    {
        if (x.Length == 0) return [];
        var max = x.Max();
        var e = x.Select(v => MathF.Exp(v - max)).ToArray();
        var sum = e.Sum();
        return [.. e.Select(v => v / sum)];
    }

    /// <summary>size×size RGB bytes (row by row, 3 per pixel) normalised to ((v/255) − mean) / std per channel.</summary>
    public static DenseTensor<float> Tensor(byte[] rgb, int size, OnnxOptions o, bool nhwc = false)
    {
        if (rgb.Length != size * size * 3) throw new ArgumentException($"Expected {size}×{size} RGB pixels.");
        var (mean, std) = (o.MeanOrDefault, o.StdOrDefault);
        var t = new DenseTensor<float>(nhwc ? [1, size, size, 3] : [1, 3, size, size]);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                for (var c = 0; c < 3; c++)
                {
                    var v = (rgb[(y * size + x) * 3 + c] / 255f - mean[c]) / std[c];
                    if (nhwc) t[0, y, x, c] = v; else t[0, c, y, x] = v;
                }
        return t;
    }

    public void Dispose() { _session?.Dispose(); _gate.Dispose(); }
}
