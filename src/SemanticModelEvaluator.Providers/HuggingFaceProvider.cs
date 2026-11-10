using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers.Onnx;

namespace SemanticModelEvaluator.Providers;

/// <summary>
/// Hugging Face Hub adapter. Discovery/search is network-gated; downloads are pinned to a commit SHA and verified against
/// LFS SHA-256. Repository code (*.py) is never downloaded or executed. Cached models remain usable offline.
/// </summary>
public sealed class HuggingFaceProvider : IAcquiringModelProvider, ILocalCacheAwareProvider
{
    public const string ProviderId = "huggingface";
    public const string TokenName = "huggingface";
    public static readonly Uri BaseUri = new("https://huggingface.co/");

    public static readonly string[] Featured =
    [
        "sentence-transformers/all-MiniLM-L6-v2",
        "BAAI/bge-small-en-v1.5",
        "intfloat/e5-small-v2",
        "sentence-transformers/paraphrase-MiniLM-L3-v2"
    ];

    private static readonly string[] SupportFiles =
        ["vocab.txt", "config.json", "tokenizer_config.json", "special_tokens_map.json", "1_Pooling/config.json", "modules.json"];

    private static readonly string[] OnnxFull = ["onnx/model.onnx", "model.onnx"];
    private static readonly string[] OnnxQuantized = ["onnx/model_qint8_avx512.onnx", "onnx/model_quint8_avx2.onnx", "onnx/model_int8.onnx", "onnx/model_quantized.onnx", "model_quantized.onnx"];

    private readonly HttpClient http;
    private readonly NetworkPolicy network;
    private readonly ISecretStore secrets;
    private readonly SettingsStore settings;
    private readonly string? cacheRootOverride;

    public HuggingFaceProvider(HttpClient http, NetworkPolicy network, ISecretStore secrets, SettingsStore settings, string? cacheRoot = null)
    {
        this.http = http;
        this.network = network;
        this.secrets = secrets;
        this.settings = settings;
        cacheRootOverride = cacheRoot;
    }

    public string Id => ProviderId;
    public string DisplayName => "Hugging Face";
    public bool UsesNetwork => true;
    public string PrivacyNotice => "Search and download contact huggingface.co (model names only). Your datasets are never uploaded; inference runs locally.";
    public string CacheRoot => cacheRootOverride ?? Path.Combine(AppPaths.Models, "hf");

    public bool HasToken
    {
        get => !string.IsNullOrEmpty(secrets.Read(TokenName));
    }

    public void SetToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) secrets.Delete(TokenName);
        else secrets.Write(TokenName, token.Trim());
    }

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var cached = CachedFolders().Count();
        var detail = $"{cached} cached model(s); token {(HasToken ? "stored" : "not set")}";
        return Task.FromResult(network.OfflineMode ? new ProviderHealth(true, "Offline (cache only)", detail) : ProviderHealth.Ok(detail));
    }

    private IEnumerable<string> CachedFolders() => Directory.Exists(CacheRoot)
        ? Directory.EnumerateDirectories(CacheRoot).SelectMany(Directory.EnumerateDirectories)
            .Where(d => !Path.GetFileName(d).Contains(".partial", StringComparison.Ordinal) && File.Exists(Path.Combine(d, SmevalModelManifest.FileName)))
        : [];

    public IReadOnlyList<ModelDescriptor> ListCached() =>
        [.. CachedFolders().Select(d => LocalOnnxProvider.Describe(d, ProviderId, SmevalModelManifest.TryLoad(d)?.RepoId)).Select(Decorate)];

    private static ModelDescriptor Decorate(ModelDescriptor d)
    {
        d.ProviderName = "Hugging Face";
        d.SourceUrl = new Uri(BaseUri, d.Identity.ModelId).ToString();
        d.Properties["cached"] = "true";
        return d;
    }

    public async Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken cancellationToken)
    {
        var cached = ListCached();
        if (!query.AllowNetwork || network.OfflineMode) return cached;
        var remote = new List<ModelDescriptor>();
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            foreach (var id in Featured)
            {
                remote.Add(Describe(await GetInfoAsync(id, null, cancellationToken).ConfigureAwait(false)));
            }
        }
        else
        {
            remote.AddRange((await SearchAsync(query.Text, cancellationToken).ConfigureAwait(false)).Select(Describe));
        }
        var cachedIds = cached.Select(c => c.Identity.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in remote) if (cachedIds.Contains(r.Identity.ModelId)) r.Properties["cachedRevisionAvailable"] = "true";
        return [.. cached, .. remote.Where(r => !cached.Any(c => c.Identity.ModelId == r.Identity.ModelId && c.Identity.Revision == r.Identity.Revision))];
    }

    private async Task<JsonNode> GetJsonAsync(Uri uri, string purpose, CancellationToken ct)
    {
        network.EnsureAllowed(purpose, uri);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        AddAuth(req);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Hugging Face denied access. The model may be gated or the token lacks permission.");
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        // Token is only ever sent to huggingface.co over HTTPS.
        if (req.RequestUri is { Scheme: "https", Host: "huggingface.co" })
        {
            string? token = secrets.Read(TokenName);
            if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<IReadOnlyList<JsonNode>> SearchAsync(string text, CancellationToken ct)
    {
        var uri = new Uri(BaseUri, $"api/models?search={Uri.EscapeDataString(text)}&full=true&limit=30&sort=downloads&direction=-1");
        var arr = await GetJsonAsync(uri, "Hugging Face model search", ct).ConfigureAwait(false);
        return [.. arr.AsArray().Where(n => n is not null).Select(n => n!)];
    }

    public Task<JsonNode> GetInfoAsync(string repoId, string? revision, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repoId, @"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new ArgumentException("Model ID must be an owner/name repository identifier.");
        var path = $"api/models/{repoId}" + (revision is null ? "" : $"/revision/{Uri.EscapeDataString(revision)}") + "?blobs=true";
        return GetJsonAsync(new Uri(BaseUri, path), "Hugging Face model metadata", ct);
    }

    public static (string? Onnx, string? Problem) ChooseOnnx(IReadOnlyCollection<string> files, bool preferQuantized)
    {
        var order = preferQuantized ? OnnxQuantized.Concat(OnnxFull) : OnnxFull.Concat(OnnxQuantized);
        var onnx = order.FirstOrDefault(files.Contains);
        if (onnx is null) return (null, "No ONNX export in this repository. Use imported predictions to evaluate it.");
        if (!files.Contains("vocab.txt")) return (onnx, "Tokenizer is not BERT WordPiece (no vocab.txt); native execution unsupported in V0.");
        return (onnx, null);
    }

    public static (string? Query, string? Document) KnownPrefixes(string repoId)
    {
        var id = repoId.ToLowerInvariant();
        if (id.Contains("e5-")) return ("query: ", "passage: ");
        if (id.StartsWith("baai/bge-") && id.Contains("-en-v1.5")) return ("Represent this sentence for searching relevant passages: ", null);
        return (null, null);
    }

    public ModelDescriptor Describe(JsonNode info)
    {
        var id = info["id"]?.GetValue<string>() ?? info["modelId"]!.GetValue<string>();
        var sha = info["sha"]?.GetValue<string>() ?? "main";
        var siblings = info["siblings"]?.AsArray().Select(s => s!["rfilename"]!.GetValue<string>()).ToList() ?? [];
        var sizes = info["siblings"]?.AsArray().Where(s => s?["size"] is not null).ToDictionary(s => s!["rfilename"]!.GetValue<string>(), s => s!["size"]!.GetValue<long>()) ?? [];
        bool gated = info["gated"] is JsonValue g && !(g.TryGetValue<bool>(out var gb) && !gb);
        var license = info["cardData"]?["license"]?.ToString() ?? info["tags"]?.AsArray().Select(t => t?.ToString()).FirstOrDefault(t => t?.StartsWith("license:") == true)?["license:".Length..] ?? "unknown";
        var (onnx, problem) = ChooseOnnx(siblings, settings.Current.PreferQuantizedOnnx);
        long? size = onnx is not null && sizes.TryGetValue(onnx, out var s0) ? s0 + SupportFiles.Sum(f => sizes.GetValueOrDefault(f)) : null;
        var tags = info["tags"]?.AsArray().Select(t => t?.ToString() ?? "").ToList() ?? [];
        var pipeline = info["pipeline_tag"]?.ToString();
        var tasks = new List<EvalTask>();
        if (pipeline is "sentence-similarity" or "feature-extraction" || tags.Contains("sentence-transformers")) tasks.AddRange([EvalTask.Retrieval, EvalTask.SemanticSimilarity]);
        else if (pipeline == "text-classification") tasks.Add(EvalTask.Classification);
        else if (pipeline == "text-ranking") tasks.Add(EvalTask.Reranking);
        else if (pipeline is "text-generation" or "text2text-generation") tasks.Add(EvalTask.Generative);
        else if (pipeline is "zero-shot-image-classification" or "image-feature-extraction") tasks.Add(EvalTask.ImageTextRetrieval);
        bool embeddingTask = tasks.Contains(EvalTask.Retrieval);
        var readiness = problem is not null || !embeddingTask ? ReadinessState.ImportOnly
            : gated && !HasToken ? ReadinessState.RequiresAuthentication : ReadinessState.NeedsDownload;
        var detail = !embeddingTask ? $"Pipeline '{pipeline ?? "unknown"}' is not natively executable in V0; evaluate via imported predictions."
            : problem ?? (gated ? "Gated model: accept the license on huggingface.co and store a token." : "Compatible ONNX export available; download to use.");
        var d = new ModelDescriptor
        {
            Identity = new ModelIdentity(ProviderId, id, sha),
            DisplayName = id,
            ProviderName = "Hugging Face",
            Family = tags.FirstOrDefault(t => t is "bert" or "xlm-roberta" or "mpnet" or "roberta" or "nomic_bert" or "clip" or "t5" or "llama") ?? info["library_name"]?.ToString() ?? "",
            Tasks = tasks,
            InputModality = tasks.Contains(EvalTask.ImageTextRetrieval) ? Modality.Multimodal : Modality.Text,
            OutputModality = embeddingTask ? Modality.Embedding : Modality.Label,
            Quantization = onnx is null ? null : OnnxQuant(onnx),
            RuntimeRequirements = embeddingTask && problem is null ? "ONNX Runtime CPU (bundled)" : "External runtime (not bundled)",
            DownloadSizeBytes = size,
            License = license,
            IsLocal = false,
            Readiness = readiness,
            ReadinessDetail = detail,
            Execution = ExecutionKind.None,
            SourceUrl = new Uri(BaseUri, id).ToString(),
            IsGated = gated
        };
        if (onnx is not null) d.Properties["onnxFile"] = onnx;
        if (info["config"]?["auto_map"] is not null) d.Properties["declaresCustomCode"] = "true";
        return d;
    }

    private static string OnnxQuant(string f) => f.Contains("int8") || f.Contains("quant") ? "INT8" : f.Contains("fp16") ? "FP16" : f.Contains("q4") ? "Q4" : "FP32";

    /// <summary>
    /// Semantic-search selectors list only embedding models (sentence-similarity / feature-extraction / sentence-transformers), never
    /// text-generation, classification or reranking models whose outputs cannot be ranked against a corpus.
    /// </summary>
    public static bool IsSemanticSearchModel(ModelDescriptor d) =>
        d.Tasks.Contains(EvalTask.Retrieval) && !d.Tasks.Contains(EvalTask.Generative) && d.OutputModality == Modality.Embedding;

    public async Task<ModelDescriptor> AcquireAsync(ModelDescriptor model, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        var repoId = model.Identity.ModelId;
        var info = await GetInfoAsync(repoId, model.Identity.Revision == "main" ? null : model.Identity.Revision, cancellationToken).ConfigureAwait(false);
        var sha = info["sha"]!.GetValue<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$"))
            throw new InvalidDataException("Hub did not return an immutable commit SHA.");
        var siblings = info["siblings"]!.AsArray().ToDictionary(s => s!["rfilename"]!.GetValue<string>(), s => s!);
        var (onnx, problem) = ChooseOnnx(siblings.Keys, settings.Current.PreferQuantizedOnnx);
        if (model.Properties.TryGetValue("onnxFile", out var requested) && siblings.ContainsKey(requested)) onnx = requested;
        if (problem is not null || onnx is null) throw new NotSupportedException(problem);

        var files = new List<string> { onnx };
        if (siblings.ContainsKey(onnx + "_data")) files.Add(onnx + "_data");
        files.AddRange(SupportFiles.Where(siblings.ContainsKey));
        if (files.Any(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Refusing to download repository code.");

        var target = Path.Combine(CacheRoot, repoId.Replace("/", "__", StringComparison.Ordinal), sha);
        if (Directory.Exists(target))
        {
            var cached = SmevalModelManifest.TryLoad(target);
            if (cached?.RepoId != repoId || cached.Revision != sha)
                throw new InvalidDataException("Existing cache identity differs. It will not be overwritten.");
            foreach (var file in cached.Files)
            {
                var existing = SafeChild(target, file.Path);
                if (!File.Exists(existing) || ImportedPredictionsProvider.HashFile(existing) != file.Sha256)
                    throw new InvalidDataException("Existing model cache is incomplete or modified. Remove this version explicitly before downloading again.");
            }
            return Decorate(LocalOnnxProvider.Describe(target, ProviderId, repoId));
        }
        var staging = target + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        long total = files.Sum(f => siblings[f]["size"]?.GetValue<long>() ?? 0), done = 0;
        var manifest = new SmevalModelManifest
        {
            Provider = ProviderId,
            RepoId = repoId,
            Revision = sha,
            License = info["cardData"]?["license"]?.ToString() ?? "unknown",
            OnnxFile = onnx,
            Gated = model.IsGated,
            DeclaresCustomCode = info["config"]?["auto_map"] is not null,
            DownloadedAt = DateTimeOffset.UtcNow
        };
        (manifest.QueryPrefix, manifest.DocumentPrefix) = KnownPrefixes(repoId);

        foreach (var file in files)
        {
            var node = siblings[file];
            var expected = node["lfs"]?["sha256"]?.GetValue<string>();
            var dest = SafeChild(staging, file);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var actual = await DownloadAsync(repoId, sha, file, dest, b =>
            {
                progress?.Report(new ProgressInfo($"Downloading {repoId}/{file}", total > 0 ? (double)(done + b) / total : null));
            }, cancellationToken).ConfigureAwait(false);
            if (expected is not null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 mismatch for {file}: expected {expected}, got {actual}.");
            var len = new FileInfo(dest).Length;
            done += len;
            manifest.Files.Add(new ManifestFile(file, len, actual));
        }
        manifest.Save(staging);
        Directory.Move(staging, target);
        progress?.Report(new ProgressInfo("Download complete; hashing model", 1));
        var d = Decorate(LocalOnnxProvider.Describe(target, ProviderId, repoId));
        if (manifest.DeclaresCustomCode) d.Properties["declaresCustomCode"] = "true";
        return d;
    }

    private async Task<string> DownloadAsync(string repoId, string sha, string file, string dest, Action<long> onBytes, CancellationToken ct)
    {
        var uri = new Uri(BaseUri, $"{repoId}/resolve/{sha}/{string.Join('/', file.Split('/').Select(Uri.EscapeDataString))}");
        network.EnsureAllowed("Hugging Face model download", uri);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        AddAuth(req);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"Access to {repoId} denied (gated model or missing token).");
        resp.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(dest))
        {
            var buf = new byte[1 << 16];
            long n = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buf, 0, read);
                n += read;
                onBytes(n);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (model.LocalPath is null)
        {
            var r = new ValidationReport();
            if (model.Readiness == ReadinessState.ImportOnly) return r.Warn(model.ReadinessDetail);
            return r.Info("Not downloaded yet. " + model.ReadinessDetail);
        }
        var report = LocalOnnxProvider.ValidateFolder(model);
        if (model.Properties.ContainsKey("declaresCustomCode"))
            report.Warn("Repository declares custom code (auto_map). It was NOT downloaded or executed; only the ONNX graph is used.");
        return report;
    }, cancellationToken);

    public Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken cancellationToken)
    {
        if (model.LocalPath is null || !Directory.Exists(model.LocalPath))
        {
            var cached = ListCached().FirstOrDefault(c => c.Identity.ModelId == model.Identity.ModelId && c.Identity.Revision == model.Identity.Revision);
            if (cached is null) throw new InvalidOperationException($"{model.Identity.Key} is not downloaded. Download it in the Model Hub first.");
            model.LocalPath = cached.LocalPath;
        }
        return LocalOnnxProvider.LoadFolderAsync(model, options, cancellationToken);
    }

    public void DeleteCached(ModelDescriptor model)
    {
        if (model.LocalPath is null) return;
        var full = Path.GetFullPath(model.LocalPath);
        var allowed = Path.GetFullPath(CacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || full == Path.GetFullPath(CacheRoot) ||
            !CachedFolders().Any(d => string.Equals(Path.GetFullPath(d), full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Refusing to delete anything other than a registered model version inside the cache.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Refusing to delete a cache containing links.");
        Directory.Delete(full, recursive: true);
    }

    private static string SafeChild(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "."))
            throw new InvalidDataException("Invalid model asset path.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model asset path escapes its cache directory.");
        return full;
    }
}
