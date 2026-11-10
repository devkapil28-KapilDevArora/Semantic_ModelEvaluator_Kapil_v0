using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Providers.EvalKit;

namespace SemanticModelEvaluator.Providers;

public enum PsapiCorpus { Text, Image }

/// <summary>
/// Contract facts for one PSAPI semantic-search model, taken from the authoritative source
/// (ASG-SearchPlatform, branch develop, ExternalBackends/psapi-embeddings-backend: README.md, Model.cpp, PsApiModels.cpp, packages.config).
/// See docs/PSAPI_DISCOVERY.md. Nothing here is inferred from a model nickname.
/// </summary>
public sealed record PsapiModelSpec(
    string ModelId,
    string DisplayName,
    PsapiCorpus Corpus,
    int Dimensions,
    int MaxInputTokens,
    string ApiPackage,
    string ModelPackageId,
    string Notes)
{
    public string CorpusModality => Corpus == PsapiCorpus.Image ? "image" : "text";
    /// <summary>The backend accepts textSource=filename only for text-corpus models (image models always read files).</summary>
    public bool SupportsFilenameTextSource => Corpus == PsapiCorpus.Text;
}

/// <summary>Local availability of the EvalKit CLI and the built PSAPI external backend. Never contains credentials.</summary>
public sealed record PsapiBackendStatus(
    string? CliPath,
    string? RepositoryRoot,
    string? PackageDirectory,
    IReadOnlyDictionary<string, string> PackageVersions,
    string? SourceBranch,
    string? SourceCommit)
{
    public const string BackendRelativePath = @"bin\EvalKit.Backend.Psapi\Release\x64";
    public const string BackendSourceRelativePath = @"ExternalBackends\psapi-embeddings-backend";

    public string BuildId { get; init; } = "auto";
    public string BuildLabel { get; init; } = "Auto-discovered";
    public string? RuntimeVersion { get; init; }
    public string? Fingerprint { get; init; }
    public string? ContractError { get; init; }
    public IReadOnlyDictionary<string, string> ModelIssues { get; init; } = new Dictionary<string, string>();

    public bool CanRun => ContractError is null && CliPath is not null && PackageDirectory is not null;

    public string Summary => ContractError is not null
        ? $"Not ready: {ContractError}"
        : CanRun
        ? $"Ready: EvalKit CLI and PSAPI backend found ({RepositoryRoot}; branch {SourceBranch ?? "unknown"}, commit {Short(SourceCommit)})."
        : CliPath is null
            ? "Not ready: the EvalKit CLI was not found. In an authorized ASG-SearchPlatform checkout run 'dotnet build EvalKit/src/EvalKit.CLI -c Release', or set the CLI path under EvalKit settings."
            : RepositoryRoot is null
                ? "Not ready: the ASG-SearchPlatform checkout that contains the CLI could not be located. Set the repository root under EvalKit settings."
                : $"Not ready: the PSAPI backend is not built ({BackendRelativePath}\\EvalKit.Backend.Psapi.exe missing). Run '{BackendSourceRelativePath}\\build.ps1 -Configuration Release' " +
                  "(needs Visual Studio C++ build tools and access to the ASG-SearchPlatform-Input package feed; with VS 2026 add -p:PlatformToolset=v145 to the MSBuild command).";

    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "unknown" : s.Length > 12 ? s[..12] : s;

    public static PsapiBackendStatus Probe(EvalKitCli cli)
    {
        var cliPath = cli.Locate();
        var root = cli.LocateRepositoryRoot();
        string? package = null;
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? branch = null, commit = null;
        if (root is not null)
        {
            var dir = Path.Combine(root, BackendRelativePath);
            if (File.Exists(Path.Combine(dir, "EvalKit.Backend.Psapi.exe")) && File.Exists(Path.Combine(dir, "backend.manifest.json"))) package = dir;
            try
            {
                var config = Path.Combine(root, BackendSourceRelativePath, "packages.config");
                if (File.Exists(config))
                    foreach (var p in XDocument.Load(config).Root?.Elements("package") ?? [])
                        if (p.Attribute("id")?.Value is { } id && p.Attribute("version")?.Value is { } v) versions[id] = v;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
            (branch, commit) = ReadGitHead(root);
        }
        return new(cliPath, root, package, versions, branch, commit);
    }

    /// <summary>Reads the checked-out branch and commit from .git without running git. Returns nulls when unavailable.</summary>
    public static (string? Branch, string? Commit) ReadGitHead(string root)
    {
        try
        {
            var git = Path.Combine(root, ".git");
            var headPath = Path.Combine(git, "HEAD");
            if (!File.Exists(headPath)) return (null, null);
            var head = File.ReadAllText(headPath).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return (null, head);
            var reference = head[5..];
            var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference[11..] : reference;
            var refFile = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile)) return (branch, File.ReadAllText(refFile).Trim());
            var packed = Path.Combine(git, "packed-refs");
            if (File.Exists(packed))
                foreach (var line in File.ReadLines(packed))
                    if (line.EndsWith(" " + reference, StringComparison.Ordinal)) return (branch, line[..line.IndexOf(' ')]);
            return (branch, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }
}

/// <summary>
/// PSAPI semantic-search adapter. PSAPI models are exposed by the EvalKit external backend <c>EvalKit.Backend.Psapi</c>
/// (JSON-RPC over stdio, manifest protocol 2), which runs locally on CPU from local PerceptiveShell packages — no endpoint,
/// network call or credential is involved at run time. A live evaluation is therefore an EvalKit job:
/// <c>evalkit run &lt;job.json&gt; --output-dir DIR</c> with <c>backend.parameters.modelId</c> = spacev6 | florence | genoa | palermo.
/// The run output (metrics envelope + query results) is loaded through the normal EvalKit loader.
/// Captured PSAPI runs (verified cloud bundles) are also discovered.
/// </summary>
public sealed class PsapiProvider : IModelProvider
{
    public const string ProviderId = "psapi";
    public const string SourceRepository = "https://dev.azure.com/microsoft/OS.Developer/_git/ASG-SearchPlatform";
    public const string SourceBranch = "develop";
    public const string SourcePath = "ExternalBackends/psapi-embeddings-backend";
    public const string SourceCommitInspected = "0527efc2519feabfdafbfeec6f1b4caccc97dd2d";
    public const string Scoring = "cosine similarity of L2-normalised embeddings mapped to (cos + 1) / 2, clamped to [0, 1]; higher is more similar";
    public const string InvocationNote =
        "PSAPI models run as a whole-dataset EvalKit job (local CPU backend), not per item. Choose 'Model provider — invoke for a new evaluation', then the PSAPI provider and an installed model.";

    /// <summary>The four PSAPI models exposed by the authoritative backend (PsApiModels.cpp / README.md).</summary>
    public static IReadOnlyList<PsapiModelSpec> Catalog { get; } =
    [
        new("spacev6", "Space v6", PsapiCorpus.Text, 100, 512, "PSAPI semtext (ps-semtextapi, ps-EmbeddingLookup_spacev6api)", "EmbeddingsLookup-SpaceV6",
            "Text query → text corpus. Smallest embedding (100-d)."),
        new("florence", "Florence", PsapiCorpus.Image, 768, 77, "PSAPI embtext + embimg (FlorenceCompliantD3)", "EmbeddingsLookup-FlorenceCompliantD3",
            "Text query → image corpus (image paths). Query token budget 77."),
        new("genoa", "Genoa", PsapiCorpus.Image, 1024, 96, "PSAPI Genoa text + vision", "EmbeddingsLookup-Genoa",
            "Text query → image corpus (image paths). Query token budget 96."),
        new("palermo", "Palermo", PsapiCorpus.Text, 768, 512, "PSAPI palermoapi (experimental package)", "palermo_transformer-onnx-all-onnx-1.17-default-int-encrypted",
            "Text query → text corpus. Uses the experimental Palermo API package."),
    ];

    public static PsapiModelSpec? Find(string modelId) => Catalog.FirstOrDefault(s => string.Equals(s.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

    public const string BlockedReason = InvocationNote;

    private readonly Func<IReadOnlyList<(string Folder, string Label)>> bundles;
    private readonly Func<PsapiBackendStatus>? backend;

    public PsapiProvider() : this(() => EvalKitCloudService.CachedBundles()) { }
    public PsapiProvider(Func<IReadOnlyList<(string Folder, string Label)>> bundles, Func<PsapiBackendStatus>? backend = null)
    {
        this.bundles = bundles; this.backend = backend;
    }

    public string Id => ProviderId;
    public string DisplayName => "PSAPI (Genoa, Florence, Palermo, Space v6)";
    public bool UsesNetwork => false;
    public string PrivacyNotice => "PSAPI models run locally on CPU through the EvalKit backend; dataset content stays on this machine. Captured runs are read from verified local bundles.";

    public PsapiBackendStatus Status() => backend?.Invoke() ?? new PsapiBackendStatus(null, null, null, new Dictionary<string, string>(), null, null);

    public static string Revision(PsapiModelSpec spec, PsapiBackendStatus status) =>
        status.PackageVersions.TryGetValue(spec.ModelPackageId, out var v) ? $"{spec.ModelPackageId}@{v}" : "package version unknown";

    /// <summary>Descriptor for one catalog model. Properties carry only non-secret contract facts.</summary>
    public static ModelDescriptor Describe(PsapiModelSpec spec, PsapiBackendStatus status) => new()
    {
        Identity = new ModelIdentity(ProviderId, spec.ModelId, Revision(spec, status)),
        DisplayName = spec.DisplayName,
        ProviderName = "PSAPI",
        Family = "PSAPI semantic search (EvalKit external backend)",
        Tasks = [EvalTask.Retrieval],
        InputModality = spec.Corpus == PsapiCorpus.Image ? Modality.Multimodal : Modality.Text,
        OutputModality = Modality.Embedding,
        EmbeddingDimension = spec.Dimensions,
        RuntimeRequirements = "Windows x64, CPU; EvalKit CLI + built EvalKit.Backend.Psapi from an authorized ASG-SearchPlatform checkout",
        License = "Microsoft internal (PerceptiveShell packages) — internal evaluation only",
        IsLocal = true,
        Readiness = status.CanRun ? ReadinessState.Ready : ReadinessState.NotInstalled,
        ReadinessDetail = status.Summary + " " + InvocationNote,
        Execution = ExecutionKind.ExternalProcess,
        SourceUrl = SourceRepository,
        Properties = Parameters(spec, status)
    };

    public static Dictionary<string, string> Parameters(PsapiModelSpec spec, PsapiBackendStatus? status = null) => new()
    {
        ["modelId"] = spec.ModelId,
        ["corpus_modality"] = spec.CorpusModality,
        ["query_modality"] = "text",
        ["dimensions"] = spec.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["max_input_tokens"] = spec.MaxInputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["similarity"] = Scoring,
        ["normalization"] = "L2 (backend)",
        ["api_package"] = spec.ApiPackage,
        ["source"] = $"{SourceRepository} @ {status?.SourceBranch ?? SourceBranch} : {SourcePath}",
        ["source_commit"] = status?.SourceCommit ?? SourceCommitInspected,
        ["invocation"] = "evalkit run (external backend, local CPU)"
    };

    /// <summary>Legacy captured-run descriptor (a verified cloud bundle recording model + PSAPI version).</summary>
    public static ModelDescriptor Describe(string model, string psapiVersion, string? folder = null) => new()
    {
        Identity = new ModelIdentity(ProviderId, model, "psapi-" + psapiVersion),
        DisplayName = $"{model} (psapi-{psapiVersion})",
        ProviderName = "PSAPI",
        Family = "PSAPI backend captured by EvalKit",
        Tasks = [EvalTask.Retrieval],
        InputModality = Modality.Text,
        OutputModality = Modality.Scores,
        RuntimeRequirements = "Captured EvalKit results",
        Readiness = ReadinessState.ImportOnly,
        ReadinessDetail = "Captured results available; no model is invoked when loading them.",
        Execution = ExecutionKind.None,
        LocalPath = folder,
        Properties = new() { ["psapi_version"] = psapiVersion, ["origin"] = "captured EvalKit bundle" }
    };

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        var s = Status();
        return Task.FromResult(new ProviderHealth(true, s.CanRun ? "Local backend ready" : "Captured results only until the backend is built", s.Summary));
    }

    public Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken ct)
    {
        var status = Status();
        var found = new Dictionary<string, ModelDescriptor>();
        foreach (var spec in Catalog)
        {
            var d = Describe(spec, status);
            if (string.IsNullOrWhiteSpace(query.Text) || d.DisplayName.Contains(query.Text, StringComparison.OrdinalIgnoreCase) || spec.ModelId.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                found.TryAdd(d.Identity.Key, d);
        }
        foreach (var (folder, _) in bundles())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "result-manifest.json")));
                if (o?["model"]?.GetValue<string>() is not { Length: > 0 } model || o["psapi_version"]?.GetValue<string>() is not { Length: > 0 } v) continue;
                var d = Describe(model, v, folder);
                if (string.IsNullOrWhiteSpace(query.Text) || d.DisplayName.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                    found.TryAdd(d.Identity.Key, d);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return Task.FromResult<IReadOnlyList<ModelDescriptor>>([.. found.Values]);
    }

    public Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken ct)
    {
        var r = new ValidationReport();
        if (model.Readiness == ReadinessState.ImportOnly) return Task.FromResult(r.Info("Captured EvalKit results can be compared; no model is invoked."));
        if (Find(model.Identity.ModelId) is null) return Task.FromResult(r.Error($"'{model.Identity.ModelId}' is not one of the PSAPI backend's models (spacev6, florence, genoa, palermo)."));
        var s = Status();
        if (s.CanRun) r.Info(s.Summary); else r.Error(s.Summary);
        r.Info(InvocationNote);
        return Task.FromResult(r);
    }

    /// <summary>PSAPI runs as a whole EvalKit job, not through the per-item runner. Use <see cref="PsapiJob"/> + <see cref="EvalKitCli.RunJobAsync"/>.</summary>
    public Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken ct) =>
        throw new NotSupportedException(InvocationNote);
}

/// <summary>Builds and validates EvalKit job descriptions for the PSAPI external backend (EvalKit/schemas/job.schema.json).</summary>
public static class PsapiJob
{
    /// <summary>Errors that make the dataset unusable for this PSAPI model. Empty = compatible.</summary>
    public static IReadOnlyList<string> CheckDataset(PsapiModelSpec spec, EvalDataset ds)
    {
        var errors = new List<string>();
        if (ds.Task is not (EvalTask.Retrieval or EvalTask.ImageTextRetrieval)) errors.Add($"PSAPI models are evaluated on retrieval datasets; this dataset's task is {ds.Task}.");
        if (ds.SourcePath is not { } path || !File.Exists(path))
            errors.Add("PSAPI needs an existing prepared dataset manifest (.smds.json or EvalKit .manifest.json).");
        if (ds.Queries.Count == 0) errors.Add("The dataset has no queries.");
        if (ds.Queries.Any(q => q.ImagePath is not null || string.IsNullOrWhiteSpace(q.Text)))
            errors.Add("PSAPI requires text queries; image-query retrieval is not supported by this backend.");
        if (ds.Documents.Count == 0) errors.Add("The dataset has no corpus documents.");
        int images = ds.Documents.Count(d => d.ImagePath is not null);
        if (spec.Corpus == PsapiCorpus.Image && images == 0)
            errors.Add($"{spec.DisplayName} indexes an IMAGE corpus (text query → image), but this dataset's corpus has no images. Use Space v6 or Palermo for text corpora.");
        if (spec.Corpus == PsapiCorpus.Text && images == ds.Documents.Count && images > 0)
            errors.Add($"{spec.DisplayName} indexes a TEXT corpus, but every corpus document in this dataset is an image. Use Florence or Genoa for image corpora.");
        return errors;
    }

    /// <summary>inlineText when every text document carries inline text; filename when text documents are file references (text-corpus models only).</summary>
    public static string TextSource(PsapiModelSpec spec, EvalDataset ds)
    {
        if (!spec.SupportsFilenameTextSource) return "inlineText";
        var text = ds.Documents.Where(d => d.ImagePath is null).ToList();
        return text.Count > 0 && text.All(d => d.Metadata is { } p && p.ContainsKey("path")) ? "filename" : "inlineText";
    }

    /// <summary>
    /// The job JSON. Paths are absolute (EvalKit resolves relative package paths against the job file's folder). No hostPrepare step is
    /// included: the app never builds code implicitly. Unknown backend parameters are rejected by the backend, so only documented ones are sent.
    /// </summary>
    public static JsonObject Build(PsapiModelSpec spec, string manifestPath, string packageDirectory, int maxResultsPerQuery, string name, string textSource = "inlineText")
    {
        if (maxResultsPerQuery is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxResultsPerQuery), "maxResultsPerQuery must be between 1 and 10000.");
        if (textSource is not ("inlineText" or "filename")) throw new ArgumentException("textSource must be inlineText or filename.", nameof(textSource));
        if (textSource == "filename" && !spec.SupportsFilenameTextSource) throw new ArgumentException($"{spec.DisplayName} does not accept textSource=filename.", nameof(textSource));
        var parameters = new JsonObject { ["modelId"] = spec.ModelId };
        if (textSource != "inlineText") parameters["textSource"] = textSource;
        return new JsonObject
        {
            ["name"] = name,
            ["manifestPath"] = Path.GetFullPath(manifestPath),
            ["backend"] = new JsonObject
            {
                ["type"] = "external",
                ["package"] = Path.GetFullPath(packageDirectory),
                ["parameters"] = parameters
            },
            ["queryConfig"] = new JsonObject { ["maxResultsPerQuery"] = maxResultsPerQuery },
            ["failFast"] = false
        };
    }

    /// <summary>Job and run names: EvalKit uses the name for output file names, so only [A-Za-z0-9_-] is kept.</summary>
    public static string RunName(PsapiModelSpec spec, string datasetName, DateTimeOffset at)
    {
        var safe = new string(datasetName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        if (safe.Length > 40) safe = safe[..40];
        return $"SMEval_{spec.ModelId}_{(safe.Length == 0 ? "dataset" : safe)}_{at.UtcDateTime:yyyyMMddHHmmss}";
    }
}
