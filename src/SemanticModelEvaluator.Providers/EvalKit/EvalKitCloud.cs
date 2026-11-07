using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.EvalKit;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers.Azure;

namespace SemanticModelEvaluator.Providers.EvalKit;

public enum ResultSensitivity { NonSensitive, Sensitive }

/// <summary>One cloud job: Experiments/&lt;experiment&gt;/psapi-&lt;version&gt;/&lt;model&gt;/&lt;dataset&gt;/ (EvalKit.Viewer ResultSource.cs).</summary>
public sealed record EvalKitCloudJob(string Experiment, string PsApiVersion, string Model, string DatasetKey, ResultSensitivity Sensitivity)
{
    public string JobKey => $"{Experiment}/psapi-{PsApiVersion}/{Model}/{DatasetKey}";
    public string BlobPrefix => $"Experiments/{Experiment}/psapi-{PsApiVersion}/{Model}/{DatasetKey}/";
    public override string ToString() => $"{Model} · psapi-{PsApiVersion} · {DatasetKey} ({Experiment}, {Sensitivity})";
}

/// <summary>Abstraction over the results container so tests never touch Azure.</summary>
public interface IResultContainer
{
    string Description { get; }
    IAsyncEnumerable<string> ListNamesAsync(string prefix, CancellationToken ct);
    IAsyncEnumerable<string> ListPrefixesAsync(string prefix, CancellationToken ct);
    Task<byte[]> ReadAsync(string blobName, CancellationToken ct);
    Task DownloadToAsync(string blobName, string path, CancellationToken ct);
}

public sealed class BlobResultContainer(BlobContainerClient client) : IResultContainer
{
    public string Description => $"{client.AccountName}/{client.Name}";

    private static Exception Map(RequestFailedException ex, string what) => ex.Status switch
    {
        401 or 403 => new AccessDeniedException($"Access denied to {what}. Your Azure CLI identity is not authorized for this container; " +
            "request access through the owning team. No other identity or container is tried."),
        404 => new FileNotFoundException($"{what} was not found."),
        _ => new IOException($"Storage request for {what} failed ({ex.Status}, {ex.ErrorCode}).")
    };

    public async IAsyncEnumerable<string> ListNamesAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var pages = client.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, ct).AsPages().GetAsyncEnumerator(ct);
        while (true)
        {
            Page<BlobItem> page;
            try { if (!await pages.MoveNextAsync()) yield break; page = pages.Current; }
            catch (RequestFailedException ex) { throw Map(ex, Description); }
            foreach (var b in page.Values) yield return b.Name;
        }
    }

    public async IAsyncEnumerable<string> ListPrefixesAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var pages = client.GetBlobsByHierarchyAsync(new GetBlobsByHierarchyOptions { Prefix = prefix, Delimiter = "/" }, ct).AsPages().GetAsyncEnumerator(ct);
        while (true)
        {
            Page<BlobHierarchyItem> page;
            try { if (!await pages.MoveNextAsync()) yield break; page = pages.Current; }
            catch (RequestFailedException ex) { throw Map(ex, Description); }
            foreach (var b in page.Values) if (b.IsPrefix) yield return b.Prefix;
        }
    }

    public async Task<byte[]> ReadAsync(string blobName, CancellationToken ct)
    {
        try { return (await client.GetBlobClient(blobName).DownloadContentAsync(ct)).Value.Content.ToArray(); }
        catch (RequestFailedException ex) { throw Map(ex, blobName); }
    }

    public async Task DownloadToAsync(string blobName, string path, CancellationToken ct)
    {
        try { await client.GetBlobClient(blobName).DownloadToAsync(path, ct); }
        catch (RequestFailedException ex) { throw Map(ex, blobName); }
    }
}

/// <summary>
/// EvalKit cloud results connector. Contract source: EvalKit.Viewer Services\Cloud (ResultSource, ResultManifest, ResultBundleFetcher).
/// Authentication is Azure CLI sign-in (as the viewer). Sensitive results are only read when the user explicitly selects the sensitive container;
/// an authorization failure is reported and never answered with a different identity or container.
/// </summary>
public sealed class EvalKitCloudService(SettingsStore settings, NetworkPolicy network, Func<ResultSensitivity, IResultContainer>? containerFactory = null)
{
    public static string CacheRoot => Path.Combine(AppPaths.Root, "evalkit-results");

    /// <summary>Configured result cache (Provider settings → EvalKit), else the default under the app root.</summary>
    public string ResultCacheRoot => settings.Current.EvalKit.ResultCacheDirectory is { Length: > 0 } configured ? Path.GetFullPath(configured) : CacheRoot;

    /// <summary>Cached bundles in the configured cache, the default cache and (read-only) the EvalKit.Viewer cache.</summary>
    public IReadOnlyList<(string Folder, string Label)> ListCachedBundles() => CachedBundles(extraRoot: ResultCacheRoot);

    public (string Account, string Container) Location(ResultSensitivity s)
    {
        var e = settings.Current.EvalKit;
        var account = Environment.GetEnvironmentVariable("EVALKIT_VIEWER_RESULTS_ACCOUNT") is { Length: > 0 } a ? a : e.ResultsAccount;
        var container = s == ResultSensitivity.Sensitive ? e.SensitiveResultsContainer : e.NonSensitiveResultsContainer;
        return (account, container);
    }

    private IResultContainer Container(ResultSensitivity s)
    {
        if (containerFactory is not null) return containerFactory(s);
        var (account, container) = Location(s);
        var uri = new Uri($"https://{account}.blob.core.windows.net/{container}");
        network.EnsureAllowed("EvalKit results", uri);
        var tenant = settings.Current.EvalKit.TenantId ?? Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        return new BlobResultContainer(new BlobContainerClient(uri, new AzureTokenSource(AzureIdentityMode.AzureCli, tenant).Credential));
    }

    public async Task<IReadOnlyList<string>> ListExperimentsAsync(ResultSensitivity s, CancellationToken ct)
    {
        var list = new List<string>();
        await foreach (var p in Container(s).ListPrefixesAsync("Experiments/", ct)) list.Add(p.TrimEnd('/').Split('/').Last());
        return list;
    }

    /// <summary>Lists jobs with a result-manifest.json under Experiments/&lt;experiment&gt;/.</summary>
    public async Task<IReadOnlyList<EvalKitCloudJob>> ListJobsAsync(string experiment, ResultSensitivity s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(experiment) || experiment.Contains('/') || experiment.Contains(".."))
            throw new ArgumentException("Enter a single experiment name.");
        var jobs = new List<EvalKitCloudJob>();
        await foreach (var name in Container(s).ListNamesAsync($"Experiments/{experiment}/", ct))
        {
            if (!name.EndsWith("/result-manifest.json", StringComparison.Ordinal)) continue;
            var parts = name.Split('/');
            // Experiments / exp / psapi-v / model / dataset / result-manifest.json
            if (parts.Length != 6 || !parts[2].StartsWith("psapi-", StringComparison.Ordinal)) continue;
            jobs.Add(new(parts[1], parts[2]["psapi-".Length..], parts[3], parts[4], s));
        }
        return jobs;
    }

    /// <summary>Validates result-manifest.json exactly as the viewer does (schema 1, identity, files with size + sha256, a metrics file).</summary>
    public static IReadOnlyList<(string Name, long Size, string Sha256)> ValidateManifest(JsonObject m, EvalKitCloudJob job)
    {
        string? S(string k) => m[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;
        if ((m["schema_version"] is JsonValue sv && sv.TryGetValue<int>(out var schema) ? schema : 0) != 1)
            throw new InvalidDataException("Unsupported result-manifest schema_version (1 is required).");
        if (S("experiment") != job.Experiment || S("blob_prefix")?.TrimEnd('/') + "/" != job.BlobPrefix || S("psapi_version") != job.PsApiVersion
            || S("model") != job.Model || S("dataset_key") != job.DatasetKey)
            throw new InvalidDataException("result-manifest.json identity does not match the selected job; the bundle is rejected.");
        var files = new List<(string, long, string)>();
        foreach (var f in m["files"] as JsonArray ?? [])
        {
            var name = f?["name"]?.GetValue<string>();
            var size = f?["size_bytes"]?.GetValue<long>();
            var sha = f?["sha256"]?.GetValue<string>();
            if (name is null || name.Contains('/') || name.Contains('\\') || name.Contains("..") || size is null or < 0
                || sha is null || !System.Text.RegularExpressions.Regex.IsMatch(sha, "^sha256:[0-9a-f]{64}$"))
                throw new InvalidDataException("result-manifest.json lists an invalid file entry.");
            files.Add((name, size.Value, sha));
        }
        if (files.Count == 0 || !files.Any(f => f.Item1.EndsWith(".metrics.json", StringComparison.Ordinal)))
            throw new InvalidDataException("Incomplete bundle: result-manifest.json lists no *.metrics.json file.");
        return files;
    }

    /// <summary>Downloads (or reuses) a verified local copy of one job and returns the cache folder. Every file is size- and SHA-256-checked.</summary>
    public async Task<string> FetchAsync(EvalKitCloudJob job, IProgress<string>? progress, CancellationToken ct)
    {
        var container = Container(job.Sensitivity);
        var manifestBytes = await container.ReadAsync(job.BlobPrefix + "result-manifest.json", ct).ConfigureAwait(false);
        var manifest = JsonNode.Parse(manifestBytes) as JsonObject ?? throw new InvalidDataException("result-manifest.json is not an object.");
        var files = ValidateManifest(manifest, job);
        var key = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant()[..16];
        var target = Path.Combine(ResultCacheRoot, job.Sensitivity.ToString(), Safe(job.Experiment), Safe(job.Model), Safe(job.DatasetKey), key);
        if (Directory.Exists(target) && files.All(f => Verify(Path.Combine(target, f.Name), f.Size, f.Sha256)))
        {
            progress?.Report("Using verified cached bundle.");
            return target;
        }
        var staging = target + ".staging-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        try
        {
            int i = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Downloading {++i}/{files.Count}: {f.Name}");
                var path = Path.Combine(staging, f.Name);
                await container.DownloadToAsync(job.BlobPrefix + f.Name, path, ct).ConfigureAwait(false);
                if (!Verify(path, f.Size, f.Sha256)) throw new InvalidDataException($"{f.Name} failed size/SHA-256 verification; the bundle was discarded.");
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, "result-manifest.json"), manifestBytes, CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, ".smeval-source.json"), new JsonObject
            {
                ["container"] = container.Description, ["sensitivity"] = job.Sensitivity.ToString(), ["job_key"] = job.JobKey,
                ["fetched_at"] = DateTimeOffset.UtcNow.ToString("O")
            }.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
            return target;
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (IOException) { }
            throw;
        }
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static bool Verify(string path, long size, string sha)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        using var s = File.OpenRead(path);
        return string.Equals("sha256:" + Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(), sha, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-verifies a cached bundle before use: the manifest must list valid file entries and every listed file must still match
    /// its size and SHA-256. Modified or incomplete caches are rejected instead of being compared.
    /// </summary>
    public static void VerifyBundle(string folder)
    {
        var manifestPath = Path.Combine(folder, "result-manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("Incomplete bundle: result-manifest.json is missing.");
        var m = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? throw new InvalidDataException("result-manifest.json is not an object.");
        string S(string k) => m[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
        var job = new EvalKitCloudJob(S("experiment"), S("psapi_version"), S("model"), S("dataset_key"), ResultSensitivity.NonSensitive);
        foreach (var (name, size, sha) in ValidateManifest(m, job))
            if (!Verify(Path.Combine(folder, name), size, sha))
                throw new InvalidDataException($"Cached bundle file '{name}' is missing or no longer matches its manifest size/SHA-256. Download the bundle again.");
    }

    /// <summary>Lists already-downloaded bundles (this app's cache and, read-only, EvalKit.Viewer's result cache).</summary>
    public static IReadOnlyList<(string Folder, string Label)> CachedBundles(string? viewerCache = null, string? extraRoot = null)
    {
        var roots = new List<string> { CacheRoot };
        if (extraRoot is { Length: > 0 } && !roots.Contains(extraRoot, StringComparer.OrdinalIgnoreCase)) roots.Add(extraRoot);
        var viewer = viewerCache ?? (Environment.GetEnvironmentVariable("EVALKIT_VIEWER_CACHE_DIR") is { Length: > 0 } v ? v
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EvalKit", "Viewer", "results"));
        roots.Add(viewer);
        var found = new List<(string, string)>();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var m in Directory.EnumerateFiles(root, "result-manifest.json", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(m)!;
                if (dir.Contains(".staging-", StringComparison.Ordinal) || dir.Contains(Path.DirectorySeparatorChar + "staging", StringComparison.Ordinal) || dir.Contains(Path.DirectorySeparatorChar + "trash", StringComparison.Ordinal)) continue;
                try
                {
                    var o = JsonNode.Parse(File.ReadAllText(m));
                    found.Add((dir, $"{o?["model"]} · psapi-{o?["psapi_version"]} · {o?["dataset_key"]} ({o?["experiment"]})" + (dir.StartsWith(viewer, StringComparison.OrdinalIgnoreCase) ? " [EvalKit.Viewer cache]" : "")));
                }
                catch (System.Text.Json.JsonException) { }
            }
        return found;
    }

    /// <summary>Classification for a cached bundle folder, derived from the container it was fetched from.</summary>
    public static string ClassificationFor(string folder)
    {
        var source = Path.Combine(folder, ".smeval-source.json");
        if (File.Exists(source) && JsonNode.Parse(File.ReadAllText(source))?["sensitivity"]?.GetValue<string>() == nameof(ResultSensitivity.Sensitive))
            return "SENSITIVE — from the sensitive EvalKit results container";
        if (File.Exists(source)) return "Non-sensitive EvalKit cloud results";
        return "Unknown classification (cached by another tool) — treat as sensitive";
    }
}

/// <summary>
/// A SCAI dataset container that <c>evalkit cache fetch</c> resolves datasets in (EvalKit.Viewer ExperimentDatasetContainers.cs):
/// published or working (staging) state × non-sensitive or sensitive. The state selects <c>EVALKIT_BLOB_PREFIX</c>.
/// Access is enforced by Azure for the signed-in user; there is no fallback to another container or identity.
/// </summary>
public sealed record EvalKitDatasetContainer(string Name, bool Working, bool Sensitive)
{
    public static IReadOnlyList<EvalKitDatasetContainer> Known { get; } =
    [
        new("eval-nonsensitive-published", false, false),
        new("eval-sensitive-published", false, true),
        new("eval-nonsensitive-working", true, false),
        new("eval-sensitive-working", true, true),
    ];
    public string BlobPrefix => Working ? "evalkit/staging/datasets" : "evalkit/published/datasets";
    public string State => Working ? "Working (staging)" : "Published";
    public string Classification => $"{(Sensitive ? "Sensitive" : "Non-sensitive")} — {State} dataset ({Name})";
    public override string ToString() => $"{State} · {(Sensitive ? "sensitive" : "non-sensitive")} ({Name})";
}

/// <summary>
/// Runs the EvalKit CLI to fetch an exact dataset: <c>cache fetch --dataset N --version V --expected-payload-hash H</c>
/// (EvalKit.Viewer DatasetFetcher.cs). The CLI must be built from an authorized ASG-SearchPlatform checkout
/// (<c>dotnet build EvalKit/src/EvalKit.CLI</c>); it authenticates with the user's own sign-in.
/// </summary>
public sealed class EvalKitCli(SettingsStore settings)
{
    public string? Locate()
    {
        var e = settings.Current.EvalKit;
        if (!string.IsNullOrWhiteSpace(e.CliPath)) return File.Exists(e.CliPath) ? e.CliPath : null;
        var roots = new[] { e.RepositoryRoot, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "ASG-SearchPlatform"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos", "ASG-SearchPlatform") };
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var bin = Path.Combine(root!, "EvalKit", "bin", "EvalKit.CLI");
            if (!Directory.Exists(bin)) continue;
            var dll = Directory.EnumerateFiles(bin, "EvalKit.CLI.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (dll is not null) return dll;
        }
        return null;
    }

    public static IReadOnlyList<string> FetchArguments(EvalKitDatasetIdentity id, string? account = null, string? container = null)
    {
        if (id.Name is null || id.Version is null || id.PayloadHash is null) throw new InvalidOperationException("Dataset name, version and payload hash are all required to fetch an exact dataset.");
        var args = new List<string> { "cache", "fetch", "--dataset", id.Name, "--version", EvalKitDatasetIdentity.NormalizeVersion(id.Version)!, "--expected-payload-hash", id.PayloadHash };
        if ((account is null) != (container is null) || (container is not null && (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(container))))
            throw new InvalidOperationException("A named dataset container needs both a storage account and a container name; refusing to fall back to the CLI default store.");
        if (account is not null && container is not null) args.AddRange(["--storage-account", account, "--container", container]);
        return args;
    }

    /// <summary>Process environment for a fetch: headless (fail fast, never prompt) and, for a container, the namespace its state selects.</summary>
    public static IReadOnlyDictionary<string, string> FetchEnvironment(EvalKitDatasetContainer? container) => container is null
        ? new Dictionary<string, string> { ["EVALKIT_HEADLESS"] = "1" }
        : new Dictionary<string, string> { ["EVALKIT_HEADLESS"] = "1", ["EVALKIT_BLOB_PREFIX"] = container.BlobPrefix };

    public async Task<string> FetchDatasetAsync(EvalKitDatasetIdentity id, EvalKitDatasetContainer? container, IProgress<string>? progress, CancellationToken ct)
    {
        var cli = Locate() ?? throw new FileNotFoundException("EvalKit CLI not found. Build it from your authorized checkout with 'dotnet build EvalKit/src/EvalKit.CLI', " +
            "or set its path under EvalKit settings.");
        var psi = new ProcessStartInfo
        {
            FileName = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : cli,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        if (psi.FileName == "dotnet") psi.ArgumentList.Add(cli);
        foreach (var a in FetchArguments(id, container is null ? null : (settings.Current.EvalKit.DatasetAccount ?? ""), container?.Name)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in FetchEnvironment(container)) psi.Environment[k] = v;
        if (!string.IsNullOrWhiteSpace(settings.Current.EvalKit.DatasetCacheDirectory)) psi.Environment["EVALKIT_CACHE_DIR"] = settings.Current.EvalKit.DatasetCacheDirectory;
        progress?.Report($"Running EvalKit CLI cache fetch for {id.Name} v{id.Version}{(container is null ? " (EvalKit default store)" : " from " + container)}...");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the EvalKit CLI.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        var err = await stderr.ConfigureAwait(false); await stdout.ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var tail = string.Join(" ", err.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
            var access = tail.Contains("403", StringComparison.Ordinal) || tail.Contains("AuthorizationPermissionMismatch", StringComparison.Ordinal) || tail.Contains("az login", StringComparison.OrdinalIgnoreCase);
            var message = $"EvalKit CLI exited with code {p.ExitCode}: {Redactor.Redact(tail)}";
            throw access ? new AccessDeniedException(message) : new InvalidOperationException(message);
        }
        return EvalKitFormat.FindCachedDataset(id, settings.Current.EvalKit.DatasetCacheDirectory)
            ?? throw new FileNotFoundException("The CLI finished but the exact dataset (name, version, payload hash) was not found in the EvalKit cache.");
    }
}
