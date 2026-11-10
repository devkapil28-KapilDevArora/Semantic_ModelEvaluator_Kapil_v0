using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Providers.EvalKit;

namespace SemanticModelEvaluator.Providers;

/// <summary>Registers existing authorized builds. Never downloads, builds, or loads native libraries into the WPF process.</summary>
public sealed class PsapiBuildRegistry(SettingsStore settings, EvalKitCli cli)
{
    public const string VerifiedRuntime = "9.0.0.309937";
    private readonly Dictionary<string, PsapiBackendStatus> inspected = [];
    private readonly object gate = new();
    public static IReadOnlyDictionary<string, string[]> ModelFiles { get; } = new Dictionary<string, string[]>
    {
        ["spacev6"] = ["semtf.quant.onnxe", "compressed_word_embeddings_spacev6.bin", "compressed_position_embeddings_spacev6.bin", "compressed_token_type_embeddings_spacev6.bin"],
        ["florence"] = ["embtxt_transformer_c.quant.onnxe", "embimg_c.quant.onnxe", "compressed_word_embeddings_florencecompliantd3.bin", "compressed_position_embeddings_florencecompliantd3.bin"],
        ["genoa"] = ["genoa_text_transformer.quant.onnxe", "genoa_vision.quant.onnxe", "compressed_word_embeddings_genoa.bin"],
        ["palermo"] = ["palermo_transformer.quant.onnxe", "palermo_transformer.data", "palermo_embeddings_qweight.data", "palermo_embeddings_scale.data"]
    };

    public static string? ContractError(string? schema, string? protocol, IEnumerable<string> runtimeVersions)
    {
        var versions = runtimeVersions.ToArray();
        return schema != "1" || protocol != "2" || versions.Length == 0 || versions.Any(v => v != VerifiedRuntime)
            ? $"Unsupported PSAPI contract. Only manifest schema 1 / protocol 2 with runtime {VerifiedRuntime} is verified. Register a matching authorized build; no API compatibility is inferred from branch names."
            : null;
    }

    public IReadOnlyList<PsapiBackendStatus> List(CancellationToken ct = default)
    {
        lock (gate)
        {
            var automatic = new PsapiBuildRegistration
            {
                Id = "auto", Label = "Auto-discovered",
                RepositoryRoot = cli.LocateRepositoryRoot() ?? "", CliPath = cli.Locate() ?? ""
            };
            if (automatic.RepositoryRoot.Length > 0) automatic.BackendDirectory = Path.Combine(automatic.RepositoryRoot, PsapiBackendStatus.BackendRelativePath);
            return new[] { automatic }.Concat(settings.Current.PsapiBuilds).Select(b =>
            {
                ct.ThrowIfCancellationRequested();
                if (!inspected.TryGetValue(b.Id, out var status)) inspected[b.Id] = status = Inspect(b, ct);
                return status;
            }).ToArray();
        }
    }

    public void Refresh() { lock (gate) inspected.Clear(); }

    public PsapiBackendStatus Register(PsapiBuildRegistration registration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registration.Label)) throw new ArgumentException("Give the build a descriptive label.");
        registration.RepositoryRoot = Path.GetFullPath(registration.RepositoryRoot);
        registration.BackendDirectory = Path.GetFullPath(registration.BackendDirectory);
        registration.CliPath = Path.GetFullPath(registration.CliPath);
        var status = Inspect(registration, ct);
        registration.Fingerprint = status.Fingerprint;
        settings.Update(s => s.PsapiBuilds.Add(registration));
        lock (gate) inspected[registration.Id] = status;
        return status;
    }

    public void Remove(string id)
    {
        if (id == "auto") throw new InvalidOperationException("The auto-discovered build is configured in EvalKit settings; only explicit registrations can be removed.");
        settings.Update(s => s.PsapiBuilds.RemoveAll(b => b.Id == id));
        lock (gate) inspected.Remove(id);
    }

    public PsapiBackendStatus Resolve(ModelDescriptor model, CancellationToken ct)
    {
        string id = model.Properties.GetValueOrDefault("psapi_build_id") ?? "auto";
        var registration = id == "auto" ? new PsapiBuildRegistration
        {
            Id = "auto", Label = "Auto-discovered", RepositoryRoot = cli.LocateRepositoryRoot() ?? "",
            CliPath = cli.Locate() ?? "", BackendDirectory = Path.Combine(cli.LocateRepositoryRoot() ?? "", PsapiBackendStatus.BackendRelativePath)
        } : settings.Current.PsapiBuilds.SingleOrDefault(b => b.Id == id)
            ?? throw new InvalidOperationException("This PSAPI build registration is missing. Restore it or choose another build.");
        var current = Inspect(registration, ct);
        if (!current.CanRun) throw new InvalidOperationException(current.Summary);
        if (current.ModelIssues.TryGetValue(model.Identity.ModelId, out var issue)) throw new InvalidOperationException(issue);
        if (!string.Equals(model.Properties.GetValueOrDefault("build_fingerprint"), current.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("PSAPI artifacts changed after selection. Refresh the build catalog and select the new build identity before running.");
        return current;
    }

    public static PsapiBackendStatus Inspect(PsapiBuildRegistration build, CancellationToken ct)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (branch, commit) = PsapiBackendStatus.ReadGitHead(build.RepositoryRoot);
        var status = new PsapiBackendStatus(File.Exists(build.CliPath) ? build.CliPath : null,
            Directory.Exists(build.RepositoryRoot) ? build.RepositoryRoot : null,
            Directory.Exists(build.BackendDirectory) ? build.BackendDirectory : null, versions, branch, commit)
        { BuildId = build.Id, BuildLabel = build.Label };
        try
        {
            if (status.CliPath is null || status.RepositoryRoot is null || status.PackageDirectory is null)
                return status with { ContractError = "Build unavailable: the registered CLI, repository or backend directory is missing. No fallback build was selected." };
            var packageConfig = Path.Combine(build.RepositoryRoot, PsapiBackendStatus.BackendSourceRelativePath, "packages.config");
            foreach (var p in XDocument.Load(packageConfig).Root?.Elements("package") ?? [])
                if (p.Attribute("id")?.Value is { } id && p.Attribute("version")?.Value is { } v) versions[id] = v;
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(build.BackendDirectory, "backend.manifest.json")));
            string[] apiNames = ["semtextapi.dll", "embtextapi.dll", "embimgapi.dll", "palermoapi.dll",
                "EmbeddingLookup_spacev6api.dll", "EmbeddingLookup_florenced3api.dll", "EmbeddingLookup_genoaapi.dll"];
            var runtimeVersions = apiNames.Select(n => FileVersionInfo.GetVersionInfo(Path.Combine(build.BackendDirectory, n)).FileVersion ?? "").ToArray();
            var error = ContractError(manifest?["schemaVersion"]?.ToString(), manifest?["protocolVersion"]?.ToString(), runtimeVersions);
            status = status with { RuntimeVersion = string.Join(", ", runtimeVersions.Distinct()), ContractError = error };
            if (error is not null) return status;
            if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                return status with { ContractError = "This verified PSAPI backend requires Windows x64." };
            string[] required = ["EvalKit.Backend.Psapi.exe", "run.ps1", "onnxruntime.dll", "DirectML.dll",
                "Microsoft.Windows.AI.MachineLearning.dll", "tokenizerapi.dll", "tokenizerdata.dll", "tokenizer_palermodata.dll", "tokenizer_gemma2data.dll"];
            foreach (var file in required)
                if (!File.Exists(Path.Combine(build.BackendDirectory, file))) throw new FileNotFoundException($"Required PSAPI runtime file is missing: {file}.");
            var modelIssues = new Dictionary<string, string>();
            foreach (var (model, files) in ModelFiles)
            {
                var missing = files.Where(f => !File.Exists(Path.Combine(build.BackendDirectory, "models", f))).ToArray();
                if (missing.Length > 0) modelIssues[model] = $"{model} assets unavailable: {string.Join(", ", missing)}. Restore the authorized model package.";
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void HashFiles(string prefix, string root, IEnumerable<string> paths)
            {
                foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    using var input = File.OpenRead(path);
                    var digest = SHA256.HashData(input);
                    hash.AppendData(Encoding.UTF8.GetBytes(prefix + Path.GetRelativePath(root, path) + "\n"));
                    hash.AppendData(digest);
                }
            }
            HashFiles("backend:", build.BackendDirectory, Directory.EnumerateFiles(build.BackendDirectory, "*", SearchOption.AllDirectories)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is not (".pdb" or ".ilk" or ".log")));
            var cliRoot = Path.GetDirectoryName(Path.GetFullPath(build.CliPath))!;
            HashFiles("cli:", cliRoot, Directory.EnumerateFiles(cliRoot, "*", SearchOption.AllDirectories)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".dll" or ".exe" or ".json"));
            HashFiles("contract:", Path.GetDirectoryName(packageConfig)!, [packageConfig]);
            var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return status with
            {
                Fingerprint = fingerprint, ModelIssues = modelIssues,
                ContractError = build.Fingerprint is not null && build.Fingerprint != fingerprint
                    ? "Registered build content changed. Register the new artifacts as a new build; previous run identities are preserved." : null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return status with { ContractError = $"Build validation failed ({ex.GetType().Name}). Verify the local package files and manifest; no fallback runtime was selected." };
        }
    }
}
