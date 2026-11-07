using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Providers;

namespace SemanticModelEvaluator.Providers.Onnx;

public enum PoolingMode { Mean, Cls, Provided }

/// <summary>Model manifest written next to downloaded/registered models (smeval-model.json).</summary>
public sealed class SmevalModelManifest
{
    public string Provider { get; set; } = "local-onnx";
    public string? RepoId { get; set; }
    public string Revision { get; set; } = "local";
    public string? License { get; set; }
    public string? OnnxFile { get; set; }
    public string? QueryPrefix { get; set; }
    public string? DocumentPrefix { get; set; }
    public PoolingMode? Pooling { get; set; }
    public string? ContentHash { get; set; }
    public long? HashedLength { get; set; }
    public DateTime? HashedWriteTimeUtc { get; set; }
    public bool Gated { get; set; }
    public bool DeclaresCustomCode { get; set; }
    public List<ManifestFile> Files { get; set; } = [];
    public DateTimeOffset? DownloadedAt { get; set; }

    public const string FileName = "smeval-model.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SmevalModelManifest? TryLoad(string dir)
    {
        var p = Path.Combine(dir, FileName);
        return File.Exists(p) ? JsonSerializer.Deserialize<SmevalModelManifest>(File.ReadAllText(p), Json) : null;
    }

    public void Save(string dir) => Core.AtomicFile.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json));
}

public sealed record ManifestFile(string Path, long Size, string Sha256);

/// <summary>Inspection of a folder that may contain a BERT-family sentence-embedding ONNX model.</summary>
public sealed class OnnxModelFolder
{
    public required string Directory { get; init; }
    public string? OnnxPath { get; init; }
    public string? VocabPath { get; init; }
    public string? ModelType { get; init; }
    public int? HiddenSize { get; init; }
    public bool LowerCase { get; init; } = true;
    public int MaxLength { get; init; } = 256;
    public PoolingMode Pooling { get; init; } = PoolingMode.Mean;
    public bool HasTokenizerJsonOnly { get; init; }
    public SmevalModelManifest? Manifest { get; init; }
    public string? Quantization { get; init; }
    public List<string> Problems { get; } = [];

    public bool IsSupported => Problems.Count == 0 && OnnxPath is not null && VocabPath is not null;

    private static readonly string[] OnnxPreference = ["onnx/model.onnx", "model.onnx", "onnx/model_quantized.onnx", "model_quantized.onnx"];

    public static OnnxModelFolder Inspect(string dir)
    {
        var manifest = SmevalModelManifest.TryLoad(dir);
        string? onnx = manifest?.OnnxFile is { } f && File.Exists(Path.Combine(dir, f)) ? Path.Combine(dir, f)
            : OnnxPreference.Select(p => Path.Combine(dir, p)).FirstOrDefault(File.Exists)
              ?? System.IO.Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories).OrderBy(x => x.Length).FirstOrDefault();
        string? vocab = File.Exists(Path.Combine(dir, "vocab.txt")) ? Path.Combine(dir, "vocab.txt") : null;
        JsonNode? config = Read(Path.Combine(dir, "config.json"));
        JsonNode? tokCfg = Read(Path.Combine(dir, "tokenizer_config.json"));
        JsonNode? pool = Read(Path.Combine(dir, "1_Pooling", "config.json"));

        var pooling = manifest?.Pooling ?? PoolingMode.Mean;
        if (pool?["pooling_mode_cls_token"]?.GetValue<bool>() == true) pooling = PoolingMode.Cls;
        int maxLen = 256;
        if (tokCfg?["model_max_length"] is JsonValue mv && mv.TryGetValue<double>(out var ml) && ml is > 0 and < 100000) maxLen = (int)Math.Min(512, ml);
        if (config?["max_position_embeddings"] is JsonValue pv && pv.TryGetValue<int>(out var pe)) maxLen = Math.Min(maxLen, pe);
        bool lower = tokCfg?["do_lower_case"] is JsonValue lv && lv.TryGetValue<bool>(out var lc) ? lc : true;
        var name = onnx is null ? "" : Path.GetFileNameWithoutExtension(onnx);
        string? quant = name.Contains("qint8") || name.Contains("quantized") || name.Contains("int8") ? "INT8" : name.Contains("fp16") ? "FP16" : onnx is null ? null : "FP32";

        var folder = new OnnxModelFolder
        {
            Directory = dir,
            OnnxPath = onnx,
            VocabPath = vocab,
            ModelType = config?["model_type"]?.GetValue<string>(),
            HiddenSize = config?["hidden_size"] is JsonValue hv && hv.TryGetValue<int>(out var hs) ? hs : null,
            LowerCase = lower,
            MaxLength = maxLen,
            Pooling = pooling,
            HasTokenizerJsonOnly = vocab is null && File.Exists(Path.Combine(dir, "tokenizer.json")),
            Manifest = manifest,
            Quantization = quant
        };
        if (onnx is null) folder.Problems.Add("No .onnx file found.");
        if (maxLen < 2) folder.Problems.Add("The tokenizer maximum length must allow at least CLS and SEP tokens.");
        if (pool is null && manifest?.Pooling is null)
            folder.Problems.Add("Pooling recipe is missing. Supply 1_Pooling/config.json or an explicit smeval-model.json pooling value; arbitrary BERT graphs are not assumed to be sentence embedders.");
        if (pool is not null)
        {
            var enabled = pool.AsObject().Where(p => p.Key.StartsWith("pooling_mode_", StringComparison.Ordinal) &&
                p.Value is JsonValue value && value.TryGetValue<bool>(out var on) && on).Select(p => p.Key).ToArray();
            if (enabled.Length != 1 || enabled[0] is not ("pooling_mode_cls_token" or "pooling_mode_mean_tokens"))
                folder.Problems.Add("Only a single CLS or mean pooling mode is supported.");
        }
        if (vocab is null) folder.Problems.Add(folder.HasTokenizerJsonOnly
            ? "Tokenizer is not BERT WordPiece (vocab.txt missing). Only WordPiece tokenizers are supported natively in V0."
            : "vocab.txt missing.");
        return folder;
    }

    private static JsonNode? Read(string path)
    {
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>Hash graph, tokenizer and recipe without modifying a registered source folder.</summary>
    public string ComputeContentHash()
    {
        if (OnnxPath is null) return "";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<string> { OnnxPath };
        if (VocabPath is not null) files.Add(VocabPath);
        files.AddRange(new[] { "config.json", "tokenizer_config.json", "special_tokens_map.json", "1_Pooling\\config.json" }
            .Select(f => Path.Combine(Directory, f)).Where(File.Exists));
        files.AddRange(System.IO.Directory.EnumerateFiles(Directory, "*.onnx_data", SearchOption.AllDirectories));
        foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(Directory, f) + "\0"));
            using var s = File.OpenRead(f);
            var buf = new byte[1 << 20];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) sha.AppendData(buf, 0, n);
        }
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Manifest?.QueryPrefix, Manifest?.DocumentPrefix, Pooling, MaxLength, LowerCase })));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>
/// Sentence-embedding inference over a BERT-family ONNX graph: WordPiece tokenisation, attention-masked mean (or CLS) pooling,
/// L2 normalisation — the same recipe FuzzyFindAnything's CompatibleModel uses, re-implemented without WPF dependencies.
/// </summary>
public sealed class OnnxEmbeddingRunner : ITextEmbeddingRunner
{
    private readonly InferenceSession session;
    private readonly BertTokenizer tokenizer;
    private readonly OnnxModelFolder folder;
    private readonly bool hasTokenTypes;
    private readonly string outputName;

    public ModelDescriptor Model { get; }
    public RuntimeInfo Runtime { get; }

    public OnnxEmbeddingRunner(ModelDescriptor model, OnnxModelFolder folder, RuntimeOptions options)
    {
        if (!folder.IsSupported) throw new NotSupportedException(string.Join(" ", folder.Problems));
        var sw = Stopwatch.StartNew();
        Model = model;
        this.folder = folder;
        using var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        if (options.Threads > 0) so.IntraOpNumThreads = options.Threads;
        session = new InferenceSession(folder.OnnxPath!, so);
        try
        {
            tokenizer = BertTokenizer.Create(folder.VocabPath!, new BertOptions { LowerCaseBeforeTokenization = folder.LowerCase, RemoveNonSpacingMarks = folder.LowerCase });
            var inputs = session.InputMetadata.Keys.ToHashSet();
            if (!inputs.Contains("input_ids") || !inputs.Contains("attention_mask") ||
                inputs.Any(i => i is not ("input_ids" or "attention_mask" or "token_type_ids")))
                throw new NotSupportedException($"ONNX graph inputs [{string.Join(", ", inputs)}] are not supported BERT inputs.");
            hasTokenTypes = inputs.Contains("token_type_ids");
            var outputs = session.OutputMetadata;
            outputName = outputs.Keys.FirstOrDefault(k => k == "sentence_embedding") ?? outputs.Keys.FirstOrDefault(k => k == "last_hidden_state")
                ?? throw new NotSupportedException("The graph has no named sentence_embedding or last_hidden_state output. Classifier logits must not be used as embeddings.");
        }
        catch { session.Dispose(); throw; }
        sw.Stop();
        Runtime = new RuntimeInfo($"ONNX Runtime {OrtEnv.Instance().GetVersionString()}", "CPU (CPUExecutionProvider)", sw.Elapsed.TotalMilliseconds,
            $"Pooling {folder.Pooling}; max length {folder.MaxLength}; output '{outputName}'");
    }

    public static string[] AvailableExecutionProviders()
    {
        return OrtEnv.Instance().GetAvailableProviders();
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken) =>
        Task.Run(() => Embed(texts, role, cancellationToken), cancellationToken);

    internal long[] Tokenize(string text)
    {
        var ids = tokenizer.EncodeToIds(text, addSpecialTokens: true, considerPreTokenization: true, considerNormalization: true);
        int max = folder.MaxLength;
        if (ids.Count <= max) return [.. ids.Select(i => (long)i)];
        var truncated = ids.Take(max - 1).Select(i => (long)i).ToList();
        truncated.Add(tokenizer.SeparatorTokenId);
        return [.. truncated];
    }

    private float[][] Embed(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        var prefix = role == EmbeddingRole.Query ? folder.Manifest?.QueryPrefix : folder.Manifest?.DocumentPrefix;
        var tokenized = texts.Select(t => Tokenize((prefix ?? "") + t)).ToArray();
        int batch = tokenized.Length, seq = tokenized.Max(t => t.Length);
        var ids = new DenseTensor<long>([batch, seq]);
        var mask = new DenseTensor<long>([batch, seq]);
        var types = new DenseTensor<long>([batch, seq]);
        for (int b = 0; b < batch; b++)
            for (int s = 0; s < seq; s++)
            {
                ids[b, s] = s < tokenized[b].Length ? tokenized[b][s] : tokenizer.PaddingTokenId;
                mask[b, s] = s < tokenized[b].Length ? 1 : 0;
            }
        var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask) };
        if (hasTokenTypes) feeds.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
        ct.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var registration = ct.Register(() => runOptions.Terminate = true);
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
        try { results = session.Run(feeds, [outputName], runOptions); }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        using var ownedResults = results;
        ct.ThrowIfCancellationRequested();
        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        if (dims.Length < 2 || dims[0] != batch || output.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException("Model returned a malformed or non-finite embedding tensor.");
        if (outputName == "last_hidden_state" && dims.Length != 3)
            throw new InvalidDataException("last_hidden_state must be a rank-3 tensor.");
        var vectors = new float[batch][];
        if (dims.Length == 2)
        {
            for (int b = 0; b < batch; b++) { vectors[b] = new float[dims[1]]; for (int d = 0; d < dims[1]; d++) vectors[b][d] = output[b, d]; }
        }
        else if (dims.Length == 3)
        {
            int hidden = dims[2];
            for (int b = 0; b < batch; b++)
            {
                var v = new float[hidden];
                if (folder.Pooling == PoolingMode.Cls) for (int d = 0; d < hidden; d++) v[d] = output[b, 0, d];
                else
                {
                    int n = tokenized[b].Length;
                    for (int s = 0; s < n; s++) for (int d = 0; d < hidden; d++) v[d] += output[b, s, d];
                    for (int d = 0; d < hidden; d++) v[d] /= n;
                }
                vectors[b] = v;
            }
        }
        else throw new NotSupportedException($"Unexpected ONNX output rank {dims.Length}.");
        return [.. vectors.Select(Core.Evaluation.EvaluationEngine.Normalize)];
    }

    public ValueTask DisposeAsync() { session.Dispose(); return ValueTask.CompletedTask; }
}
