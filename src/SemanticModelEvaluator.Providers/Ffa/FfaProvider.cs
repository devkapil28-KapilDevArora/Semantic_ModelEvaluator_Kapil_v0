using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;

namespace SemanticModelEvaluator.Providers.Ffa;

/// <summary>Line-delimited JSON transport (stdin/stdout of a worker process, or an in-memory fake in tests).</summary>
public interface IJsonLineChannel : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken ct);
    Task<string?> ReadLineAsync(CancellationToken ct);
}

public sealed class ProcessJsonLineChannel : IJsonLineChannel
{
    private readonly Process process;
    private readonly AppLog? log;

    public ProcessJsonLineChannel(string executable, IEnumerable<string> args, AppLog? log = null)
    {
        this.log = log;
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + executable);
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) this.log?.Warn("ffa-worker", "Worker wrote to stderr; content omitted to protect dataset text."); };
        process.BeginErrorReadLine();
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct) => await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        try { process.StandardInput.Close(); } catch (IOException) { }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        process.Dispose();
    }
}

public sealed class FfaProtocolException(string message, string? code = null, bool retryable = false) : Exception(message)
{
    public string? Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>Client for the FuzzyFindAnything JSON-lines protocol (snake_case, {"ok":..} envelopes, "event" side messages).</summary>
public static class FfaProtocol
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string Request(string operation, object? args = null) =>
        JsonSerializer.Serialize(new { operation, args = args ?? new { } }, Json);

    /// <summary>Reads until a result envelope arrives. Progress events are reported; memory requests are acknowledged.</summary>
    public static async Task<JsonNode?> ReadResultAsync(IJsonLineChannel channel, Action<string>? onProgress, CancellationToken ct)
    {
        while (true)
        {
            var line = await channel.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new FfaProtocolException("Worker closed the connection unexpectedly.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (node is not JsonObject obj) continue;
            if (obj["event"] is JsonNode ev)
            {
                var name = ev.GetValue<string>();
                if (name == "progress") onProgress?.Invoke(obj["message"]?.ToString() ?? "");
                else if (name == "memory_request") await channel.WriteLineAsync(Request("memory_ready"), ct).ConfigureAwait(false);
                else if (name == "startup") onProgress?.Invoke(obj["message"]?.ToString() ?? "Starting worker");
                continue;
            }
            if (obj["ok"] is JsonNode ok)
            {
                if (ok.GetValue<bool>()) return obj["result"];
                throw new FfaProtocolException(obj["error"]?.ToString() ?? "Provider error", obj["code"]?.ToString(), obj["retryable"]?.GetValue<bool>() ?? false);
            }
        }
    }

    public static async Task<JsonNode?> CallAsync(IJsonLineChannel channel, string operation, object? args, Action<string>? onProgress, CancellationToken ct)
    {
        await channel.WriteLineAsync(Request(operation, args), ct).ConfigureAwait(false);
        return await ReadResultAsync(channel, onProgress, ct).ConfigureAwait(false);
    }

    public static float[] ToVector(JsonNode? node) =>
        node?.AsArray().Select(x => x!.GetValue<float>()).ToArray() ?? throw new FfaProtocolException("Worker returned no vector.");
}

public sealed record FfaRegistration(string Id, string Executable, string? DefaultVersion, int ProtocolVersion, string Source);

public static partial class FfaRegistry
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FuzzyFindAnythingProviders");

    /// <summary>Reads provider.json registrations with the same validation rules FuzzyFindAnything applies.</summary>
    public static (List<FfaRegistration> Registrations, List<string> Problems) Discover(string directory)
    {
        var regs = new List<FfaRegistration>();
        var problems = new List<string>();
        if (!Directory.Exists(directory)) return (regs, problems);
        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            var file = Path.Combine(dir, "provider.json");
            if (!File.Exists(file)) continue;
            try
            {
                if (new FileInfo(file).Length > 64 * 1024) { problems.Add($"{file}: registration too large."); continue; }
                var node = JsonNode.Parse(File.ReadAllText(file))!;
                var id = node["id"]?.ToString() ?? "";
                var exe = node["executable"]?.ToString() ?? "";
                if (!IdPattern().IsMatch(id)) { problems.Add($"{file}: invalid id '{id}'."); continue; }
                if (!Path.IsPathFullyQualified(exe)) { problems.Add($"{id}: executable path must be absolute."); continue; }
                if (!File.Exists(exe)) { problems.Add($"{id}: executable not found ({exe})."); continue; }
                regs.Add(new FfaRegistration(id, exe, node["default_version"]?.ToString(), node["protocol_version"]?.GetValue<int>() ?? 1, file));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
            {
                problems.Add($"{file}: {ex.Message}");
            }
        }
        return (regs, problems);
    }
}

/// <summary>
/// Adapter for FuzzyFindAnything model workers and external providers (including the optional Microsoft internal provider).
/// Nothing is launched until the user asks: catalogs are refreshed explicitly; workers are started for evaluation only.
/// </summary>
public sealed class FfaProvider(SettingsStore settings, AppLog? log = null, string? registrationDirectory = null,
    Func<string, IEnumerable<string>, IJsonLineChannel>? channelFactory = null) : IModelProvider
{
    public const string ProviderId = "ffa";
    public const string MicrosoftProviderId = "microsoft";
    private readonly Func<string, IEnumerable<string>, IJsonLineChannel> factory = channelFactory ?? ((exe, args) => new ProcessJsonLineChannel(exe, args, log));
    private readonly Dictionary<string, JsonNode> catalogs = [];

    public string Id => ProviderId;
    public string DisplayName => "FuzzyFindAnything providers";
    public bool UsesNetwork => false;
    public string PrivacyNotice => "Launches locally registered provider executables. A provider may contact its own service during catalog/prepare (e.g. Microsoft internal access checks); inference runs locally.";
    public string RegistrationDirectory => registrationDirectory ?? FfaRegistry.DefaultDirectory;

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var (regs, problems) = FfaRegistry.Discover(RegistrationDirectory);
        var bindings = settings.Current.FfaBindings.Count;
        if (regs.Count == 0 && bindings == 0)
            return Task.FromResult(ProviderHealth.Missing($"No FuzzyFindAnything provider registrations in {RegistrationDirectory}." + (problems.Count > 0 ? " " + string.Join(" ", problems) : "")));
        return Task.FromResult(ProviderHealth.Ok($"{regs.Count} registration(s): {string.Join(", ", regs.Select(r => r.Id))}; {bindings} worker binding(s)" +
            (problems.Count > 0 ? "; problems: " + string.Join(" ", problems) : "")));
    }

    public void AddBinding(FfaWorkerBinding binding)
    {
        if (!Path.IsPathFullyQualified(binding.WorkerExecutable) || !File.Exists(binding.WorkerExecutable)) throw new FileNotFoundException("Worker executable not found.", binding.WorkerExecutable);
        if (!Directory.Exists(binding.ModelDirectory)) throw new DirectoryNotFoundException(binding.ModelDirectory);
        if (string.IsNullOrWhiteSpace(binding.Name)) throw new ArgumentException("Binding name is required.");
        binding.ContentHash = HashBinding(binding.WorkerExecutable, binding.ModelDirectory, binding.ProviderId);
        settings.Update(s => { s.FfaBindings.RemoveAll(b => b.Name == binding.Name); s.FfaBindings.Add(binding); });
    }

    public async Task<JsonNode?> RefreshCatalogAsync(FfaRegistration reg, Action<string>? progress, CancellationToken ct)
    {
        await using var ch = factory(reg.Executable, ["--provider-request"]);
        var result = await FfaProtocol.CallAsync(ch, "catalog", new { version = reg.DefaultVersion, refresh = true }, progress, ct).ConfigureAwait(false);
        if (result is not null) catalogs[reg.Id] = result.DeepClone();
        return result;
    }

    public async Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken cancellationToken)
    {
        var (regs, _) = FfaRegistry.Discover(RegistrationDirectory);
        var list = new List<ModelDescriptor>();
        foreach (var b in settings.Current.FfaBindings) list.Add(DescribeBinding(b));

        if (!regs.Any(r => r.Id == MicrosoftProviderId))
        {
            list.Add(new ModelDescriptor
            {
                Identity = new ModelIdentity(ProviderId, MicrosoftProviderId + "/internal-models", "n/a"),
                DisplayName = "Microsoft internal models (spacev6, palermo, florence, genoa)",
                ProviderName = "Microsoft (FFA MicrosoftProvider)",
                Family = "Microsoft internal",
                Tasks = [EvalTask.Retrieval, EvalTask.ImageTextRetrieval],
                InputModality = Modality.Multimodal,
                License = "Microsoft internal",
                Readiness = ReadinessState.NotInstalled,
                ReadinessDetail = "FuzzyFindAnything-MicrosoftProvider is not installed/registered on this PC. Install it (internal release) and register it, then refresh. Internal access is verified by the provider itself.",
                Execution = ExecutionKind.None
            });
        }

        foreach (var reg in regs)
        {
            if (query.AllowNetwork && !catalogs.ContainsKey(reg.Id))
            {
                try { await RefreshCatalogAsync(reg, null, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is FfaProtocolException or IOException or System.ComponentModel.Win32Exception)
                {
                    log?.Warn("ffa", $"Catalog for {reg.Id} failed: {ex.Message}");
                }
            }
            if (catalogs.TryGetValue(reg.Id, out var catalog)) list.AddRange(DescribeCatalog(reg, catalog));
            else list.Add(new ModelDescriptor
            {
                Identity = new ModelIdentity(ProviderId, reg.Id + "/catalog", reg.DefaultVersion ?? "default"),
                DisplayName = $"{reg.Id} provider (catalog not loaded)",
                ProviderName = reg.Id == MicrosoftProviderId ? "Microsoft (FFA MicrosoftProvider)" : "FFA provider: " + reg.Id,
                Readiness = ReadinessState.NeedsDownload,
                ReadinessDetail = "Refresh with network allowed to query this provider's catalog. The provider may contact its own service.",
                Execution = ExecutionKind.None
            });
        }
        return list;
    }

    public static IEnumerable<ModelDescriptor> DescribeCatalog(FfaRegistration reg, JsonNode catalog)
    {
        var version = catalog["version"]?.ToString() ?? reg.DefaultVersion ?? "default";
        var notice = catalog["availability_notice"]?.ToString();
        bool verified = catalog["internal_access_verified"]?.GetValue<bool>() ?? false;
        foreach (var m in catalog["models"]?.AsArray() ?? [])
        {
            if (m is null) continue;
            var kinds = m["kinds"]?.AsArray().Select(k => k?.ToString()).ToList() ?? [];
            var state = m["state"]?.ToString() ?? "";
            yield return new ModelDescriptor
            {
                Identity = new ModelIdentity(ProviderId, reg.Id + "/" + m["id"]!.ToString(), version),
                DisplayName = m["label"]?.ToString() ?? m["id"]!.ToString(),
                ProviderName = reg.Id == MicrosoftProviderId ? "Microsoft (FFA MicrosoftProvider)" : "FFA provider: " + reg.Id,
                Family = reg.Id,
                Tasks = kinds.Contains("Image") ? [EvalTask.Retrieval, EvalTask.ImageTextRetrieval] : [EvalTask.Retrieval, EvalTask.SemanticSimilarity],
                InputModality = kinds.Contains("Image") ? Modality.Multimodal : Modality.Text,
                License = reg.Id == MicrosoftProviderId ? "Microsoft internal" : "See provider",
                Readiness = state.Equals("ready", StringComparison.OrdinalIgnoreCase) || state.Equals("installed", StringComparison.OrdinalIgnoreCase)
                    ? ReadinessState.NeedsDownload
                    : reg.Id == MicrosoftProviderId && !verified ? ReadinessState.RequiresAuthentication : ReadinessState.NeedsDownload,
                ReadinessDetail = (m["detail"]?.ToString() ?? state) + (notice is null ? "" : " · " + notice) +
                    " · Prepare it in FuzzyFindAnything, then bind its worker + prepared model folder here.",
                Execution = ExecutionKind.None,
                Properties = { ["kinds"] = string.Join(",", kinds), ["state"] = state }
            };
        }
    }

    public static ModelDescriptor DescribeBinding(FfaWorkerBinding b)
    {
        bool ok = File.Exists(b.WorkerExecutable) && Directory.Exists(b.ModelDirectory) && b.ContentHash is not null;
        return new ModelDescriptor
        {
            Identity = new ModelIdentity(ProviderId, b.Name, b.ContentHash is { } hash ? "sha256-" + hash[..12] : "unverified", b.ContentHash),
            DisplayName = b.Name,
            ProviderName = string.IsNullOrEmpty(b.ProviderId) ? "FFA worker" : "FFA worker (" + b.ProviderId + ")",
            Family = "FFA external worker",
            Tasks = [EvalTask.Retrieval, EvalTask.SemanticSimilarity],
            EmbeddingDimension = b.Dimensions,
            RuntimeRequirements = "External worker process: " + Path.GetFileName(b.WorkerExecutable),
            License = "See provider",
            IsLocal = true,
            Readiness = ok ? ReadinessState.Ready : ReadinessState.Error,
            ReadinessDetail = ok ? "Ready · runs " + Path.GetFileName(b.WorkerExecutable) : "Worker/model missing, or legacy binding has no content hash. Register the binding again.",
            Execution = ExecutionKind.ExternalProcess,
            LocalPath = b.ModelDirectory,
            Properties = { ["worker"] = b.WorkerExecutable, ["ffaProvider"] = b.ProviderId }
        };
    }

    public Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var r = new ValidationReport();
        if (model.Execution != ExecutionKind.ExternalProcess) return r.Warn(model.ReadinessDetail);
        if (!model.Properties.TryGetValue("worker", out var exe) || !File.Exists(exe)) r.Error("Worker executable not found.");
        if (model.LocalPath is null || !Directory.Exists(model.LocalPath)) r.Error("Prepared model folder not found.");
        if (r.IsValid && model.Identity.ContentHash is { } expected &&
            HashBinding(exe!, model.LocalPath!, model.Properties.GetValueOrDefault("ffaProvider", "")) != expected)
            r.Error("Worker or prepared model files changed. Register a new binding; the saved version will not be substituted.");
        if (r.IsValid) r.Info("Worker binding present. Load test happens when the evaluation starts.");
        return r;
    }, cancellationToken);

    public async Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken cancellationToken)
    {
        if (model.Execution != ExecutionKind.ExternalProcess || !model.Properties.TryGetValue("worker", out var exe))
            throw new InvalidOperationException($"{model.DisplayName} is not bound to a prepared worker.");
        if (model.Identity.ContentHash is { } expected &&
            await Task.Run(() => HashBinding(exe, model.LocalPath!, model.Properties.GetValueOrDefault("ffaProvider", "")), cancellationToken).ConfigureAwait(false) != expected)
            throw new InvalidDataException("Worker or model content changed. Refusing to substitute another version.");
        var args = new List<string> { "--model", model.LocalPath!, "--threads", Math.Max(1, options.Threads == 0 ? Environment.ProcessorCount / 2 : options.Threads).ToString() };
        if (model.Properties.TryGetValue("ffaProvider", out var p) && p.Length > 0) { args.Add("--provider"); args.Add(p); }
        var sw = Stopwatch.StartNew();
        var channel = factory(exe, args);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var ready = await FfaProtocol.ReadResultAsync(channel, _ => log?.Info("ffa-worker", "Worker startup progress received."), timeout.Token).ConfigureAwait(false);
            if (ready?.ToString() != "ready") throw new FfaProtocolException("Worker did not report ready.");
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new FfaWorkerRunner(model, channel, new RuntimeInfo("FFA worker " + Path.GetFileName(exe), options.Device, sw.Elapsed.TotalMilliseconds));
    }

    private static string HashBinding(string executable, string directory, string provider)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(provider + "\0"));
        var files = new[] { executable }.Concat(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal));
        foreach (var file in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file) + "\0"));
            using var stream = File.OpenRead(file);
            hash.AppendData(System.Security.Cryptography.SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public sealed class FfaWorkerRunner(ModelDescriptor model, IJsonLineChannel channel, RuntimeInfo runtime) : ITextEmbeddingRunner
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public ModelDescriptor Model { get; } = model;
    public RuntimeInfo Runtime { get; } = runtime;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (role == EmbeddingRole.Query)
            {
                var result = new float[texts.Count][];
                for (int i = 0; i < texts.Count; i++)
                    result[i] = FfaProtocol.ToVector(await FfaProtocol.CallAsync(channel, "query", new { text = texts[i] }, null, cancellationToken).ConfigureAwait(false));
                return result;
            }
            var node = await FfaProtocol.CallAsync(channel, "documents", new { texts }, null, cancellationToken).ConfigureAwait(false);
            var arr = node?.AsArray() ?? throw new FfaProtocolException("Worker returned no document vectors.");
            if (arr.Count != texts.Count) throw new FfaProtocolException($"Worker returned {arr.Count} vectors for {texts.Count} documents.");
            return [.. arr.Select(FfaProtocol.ToVector)];
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await FfaProtocol.CallAsync(channel, "shutdown", null, null, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is FfaProtocolException or IOException or OperationCanceledException or InvalidOperationException) { }
        await channel.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }
}
