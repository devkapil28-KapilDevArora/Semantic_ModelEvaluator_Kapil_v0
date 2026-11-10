using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Security;

namespace SemanticModelEvaluator.Providers.Datasets;

public sealed record HubDatasetFile(string Path, long? Bytes, string? Sha256, string? GitOid)
{
    public bool IsTable => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".csv" or ".tsv" or ".json" or ".jsonl" or ".parquet";
    public override string ToString() => $"{Path} ({(Bytes is { } n ? $"{n / 1048576d:0.00} MB" : "size unknown")})";
}

public sealed record HubDatasetConfiguration(string Name, string Split, long? Examples, string Schema, IReadOnlyList<string> Patterns)
{
    public override string ToString() => $"{Name} / {Split} ({Examples?.ToString(CultureInfo.InvariantCulture) ?? "?"} examples)";
}

public sealed record HubDatasetInfo(string Repository, string Revision, string License, bool Restricted,
    IReadOnlyList<HubDatasetFile> Files, IReadOnlyList<HubDatasetConfiguration> Configurations)
{
    public bool CanDownload => !Restricted && !string.IsNullOrWhiteSpace(License) &&
        !License.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !License.Equals("other", StringComparison.OrdinalIgnoreCase);
    public string Access => Restricted ? "Private/gated: this public connector cannot download it."
        : CanDownload ? "Public; review and accept license before downloading."
        : "License unresolved: inspect the official dataset card. Download blocked.";
}

/// <summary>Public static Hub files only. No credentials, dataset scripts, viewer-generated rows or external inference.</summary>
public sealed class HuggingFaceDatasets(HttpClient http, NetworkPolicy network, string? cacheRoot = null)
{
    public string CacheRoot { get; } = cacheRoot ?? Path.Combine(AppPaths.Root, "datasets", "public", "huggingface");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long? N(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var i) ? i : null;
    private static IEnumerable<JsonNode> Nodes(JsonNode? n) => n is JsonArray a ? a.OfType<JsonNode>() : n is JsonObject ? [n] : [];
    private static string JoinValues(JsonNode? n) => n is JsonArray a ? string.Join(", ", a.Select(S).OfType<string>()) : S(n) ?? "";

    public static string RepositoryPath(string repository)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*(/[A-Za-z0-9_-][A-Za-z0-9_.-]*)?\z") || repository.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Enter a Hugging Face dataset repository ID (owner/name), not a URL.");
        return string.Join("/", repository.Split('/').Select(Uri.EscapeDataString));
    }

    public static void ValidateFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(s => s is "" or "." or ".."))
            throw new InvalidDataException("Unsafe dataset repository file path.");
    }

    private async Task<JsonNode> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        network.EnsureAllowed("Public dataset metadata", uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new Core.Providers.AccessDeniedException("Hugging Face denied access. This connector does not use credentials or bypass gated dataset terms.");
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("Empty Hugging Face metadata response.");
    }

    public async Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter public dataset search terms.");
        var root = await GetJsonAsync(new Uri($"https://huggingface.co/api/datasets?search={Uri.EscapeDataString(query.Trim())}&limit=20"), ct).ConfigureAwait(false);
        if (root is not JsonArray list) throw new InvalidDataException("Unexpected Hub dataset search contract.");
        return list.Select(x => S(x?["id"])).OfType<string>().ToList();
    }

    public async Task<HubDatasetInfo> InspectAsync(string repository, string revision, CancellationToken ct)
    {
        var id = RepositoryPath(repository);
        if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("A revision or branch is required; it will be resolved to a commit.");
        var root = await GetJsonAsync(new Uri($"https://huggingface.co/api/datasets/{id}/revision/{Uri.EscapeDataString(revision)}?blobs=true"), ct).ConfigureAwait(false);
        var info = Parse(root);
        Directory.CreateDirectory(CacheRoot);
        AtomicFile.WriteAllText(Path.Combine(CacheRoot, MetadataName(repository, info.Revision)), JsonSerializer.Serialize(info, Json));
        return info;
    }

    private static string MetadataName(string repository, string revision) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository + "@" + revision))) + ".metadata.json";

    public HubDatasetInfo ReadCached(string repository, string revision) =>
        JsonSerializer.Deserialize<HubDatasetInfo>(File.ReadAllText(Path.Combine(CacheRoot, MetadataName(repository, revision))), Json)
        ?? throw new InvalidDataException("Cached dataset metadata is invalid.");

    public static HubDatasetInfo Parse(JsonNode root)
    {
        string repository = S(root["id"]) ?? throw new InvalidDataException("Dataset ID missing.");
        RepositoryPath(repository);
        string sha = S(root["sha"]) ?? "";
        if (!Regex.IsMatch(sha, @"\A[0-9a-fA-F]{40}\z")) throw new InvalidDataException("The Hub did not resolve an immutable dataset revision.");
        var card = root["cardData"];
        var files = Nodes(root["siblings"]).Select(f => new HubDatasetFile(
            S(f["rfilename"]) ?? "", N(f["size"]) ?? N(f["lfs"]?["size"]), S(f["lfs"]?["sha256"]), S(f["blobId"]))).ToList();
        foreach (var f in files) ValidateFilePath(f.Path);
        var configs = new List<HubDatasetConfiguration>();
        foreach (var config in Nodes(card?["configs"]))
        {
            var name = S(config["config_name"]) ?? "default";
            var info = Nodes(card?["dataset_info"]).FirstOrDefault(i => (S(i["config_name"]) ?? "default") == name);
            foreach (var split in Nodes(config["data_files"]))
            {
                var splitName = S(split["split"]) ?? "unspecified";
                var stats = Nodes(info?["splits"]).FirstOrDefault(s => S(s["name"]) == splitName);
                var patterns = split["path"] is JsonArray a ? a.Select(S).OfType<string>().ToArray()
                    : S(split["path"]) is { } p ? [p] : Array.Empty<string>();
                configs.Add(new(name, splitName, N(stats?["num_examples"]), info?["features"]?.ToJsonString() ?? "Schema not supplied by repository", patterns));
            }
        }
        if (configs.Count == 0)
            foreach (var info in Nodes(card?["dataset_info"]))
                foreach (var split in Nodes(info["splits"]))
                    configs.Add(new(S(info["config_name"]) ?? "default", S(split["name"]) ?? "unspecified",
                        N(split["num_examples"]), info["features"]?.ToJsonString() ?? "Schema unknown", []));
        return new(repository, sha, JoinValues(card?["license"]),
            root["private"]?.ToString() == "true" || root["gated"] is { } gated && gated.ToString() != "false", files, configs);
    }

    public string FileCachePath(HubDatasetInfo info, HubDatasetFile file)
    {
        ValidateFilePath(file.Path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.Repository + "@" + info.Revision + "/" + file.Path)));
        return Path.Combine(CacheRoot, key + Path.GetExtension(file.Path));
    }

    public async Task<string> DownloadAsync(HubDatasetInfo info, HubDatasetFile file, long byteBudget,
        bool licenseAccepted, IProgress<string>? progress, CancellationToken ct)
    {
        if (!info.CanDownload || !licenseAccepted) throw new InvalidOperationException("Public access and explicit license acceptance are required.");
        if (!info.Files.Contains(file)) throw new InvalidDataException("File is not part of the selected immutable repository revision.");
        if (file.Bytes is not { } length || length < 1 || length > byteBudget)
            throw new InvalidOperationException("File size is unknown or exceeds the explicit storage budget. Select a smaller shard or increase the budget.");
        if (!Regex.IsMatch(file.Sha256 ?? "", @"\A[0-9a-fA-F]{64}\z") && !Regex.IsMatch(file.GitOid ?? "", @"\A[0-9a-fA-F]{40}\z"))
            throw new InvalidDataException("Hub file has no verifiable LFS SHA-256 or Git object hash.");
        var path = FileCachePath(info, file);
        Directory.CreateDirectory(CacheRoot);
        if (File.Exists(path))
        {
            await VerifyAsync(path, file, ct).ConfigureAwait(false);
            progress?.Report("Verified cached file; no download.");
            return path;
        }
        var part = path + ".partial";
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > length) { File.Delete(part); offset = 0; }
        if (offset < length)
        {
            var uri = new Uri($"https://huggingface.co/datasets/{RepositoryPath(info.Repository)}/resolve/{info.Revision}/{string.Join("/", file.Path.Split('/').Select(Uri.EscapeDataString))}");
            network.EnsureAllowed("Public dataset download", uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            bool resume = response.StatusCode == HttpStatusCode.PartialContent;
            if (resume && (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != length))
                throw new InvalidDataException("Download server returned an unexpected byte range.");
            if (!resume) offset = 0;
            await using var target = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long total = offset, last = offset;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > length) throw new InvalidDataException("Download exceeded the pinned file size.");
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                if (total - last >= 1048576 || total == length)
                {
                    progress?.Report($"Downloading {total / 1048576d:0.0} / {length / 1048576d:0.0} MB");
                    last = total;
                }
            }
        }
        try { await VerifyAsync(part, file, ct).ConfigureAwait(false); }
        catch (InvalidDataException) { File.Delete(part); throw; }
        File.Move(part, path);
        return path;
    }

    private static async Task VerifyAsync(string path, HubDatasetFile file, CancellationToken ct)
    {
        if (new FileInfo(path).Length != file.Bytes) throw new InvalidDataException("Dataset download is incomplete; retry the selected file.");
        await using var input = File.OpenRead(path);
        string actual;
        if (file.Sha256 is { Length: 64 })
            actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
        else
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.UTF8.GetBytes($"blob {input.Length}\0"));
            byte[] buffer = new byte[81920];
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer.AsSpan(0, n));
            actual = Convert.ToHexString(hash.GetHashAndReset());
        }
        if (!actual.Equals(file.Sha256 ?? file.GitOid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dataset file integrity check failed; the cached file must not be used.");
    }
}
