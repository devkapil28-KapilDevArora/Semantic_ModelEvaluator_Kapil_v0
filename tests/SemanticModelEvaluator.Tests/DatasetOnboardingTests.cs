using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.EvalKit;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.Datasets;
using SemanticModelEvaluator.Providers.EvalKit;
using Xunit;

namespace SemanticModelEvaluator.Tests;

public sealed class DatasetOnboardingTests : IDisposable
{
    private readonly string root = Fixtures.NewTempDir();
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Csv = "id,text,text2,score\n1,a cat,a kitten,0.8\n2,rain,sun,0.1\n";

    private static PublicDatasetRecipe Recipe => new("fixture", "Explicit similarity fixture", EvalTask.SemanticSimilarity, 10,
        [new("public/fixture", "test.csv", "default", "test", TableKind.Examples,
            new Dictionary<string, ColumnRole> { ["id"] = ColumnRole.Id, ["text"] = ColumnRole.Text,
                ["text2"] = ColumnRole.Text2, ["score"] = ColumnRole.SimilarityScore })]);

    private static JsonObject Metadata(string license = "cc0-1.0", bool gated = false) => new()
    {
        ["id"] = "public/fixture", ["sha"] = Revision, ["private"] = false, ["gated"] = gated,
        ["cardData"] = new JsonObject { ["license"] = new JsonArray(license) },
        ["siblings"] = new JsonArray(new JsonObject
        {
            ["rfilename"] = "test.csv", ["size"] = Encoding.UTF8.GetByteCount(Csv),
            ["lfs"] = new JsonObject { ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Csv))).ToLowerInvariant() }
        })
    };

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public void Live_evalkit_manifest_reference_supplies_exact_run_identity()
    {
        var path = Path.Combine(root, "live.metrics.json");
        File.WriteAllText(path, """
            {"schema_version":3,"run_id":"live",
             "inputs":{"manifest":{"name":"public image input","version":"abc123","path":"../dataset/dataset.manifest.json"},
                       "dataset_payload_hash":"sha256:abc"},
             "evalkit":{"effective_config":{"parameters":{"model_id":"genoa"}}}}
            """);
        var run = EvalKitFormat.ReadRun(path);
        Assert.Equal(new EvalKitDatasetIdentity("public image input", "abc123", "sha256:abc"), run.Dataset);
        Assert.Equal("genoa", run.BackendModelId);
        Assert.NotEmpty(run.Dataset.Mismatches(new("public image input", "different", "sha256:abc")));
    }

    [Fact]
    public void Live_evalkit_reader_follows_rankings_envelope_not_metric_rows_and_verifies_integrity()
    {
        var ranks = """{"query_id":"q1","document_id":"d1","position":1,"score":0.75}""";
        File.WriteAllText(Path.Combine(root, "ranks.jsonl"), ranks);
        File.WriteAllText(Path.Combine(root, "run.metrics.jsonl"), """{"query_id":"q1","ndcg":1}""");
        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ranks))).ToLowerInvariant();
        var envelope = new JsonObject
        {
            ["inputs"] = new JsonObject { ["dataset_payload_hash"] = "sha256:fixture" },
            ["results"] = new JsonObject { ["path"] = "ranks.jsonl", ["hash"] = hash }
        };
        var envelopePath = Path.Combine(root, "run.query-results.json");
        File.WriteAllText(envelopePath, envelope.ToJsonString());
        var path = Path.Combine(root, "run.metrics.json");
        File.WriteAllText(path, """
            {"schema_version":3,"query_count":1,"failed_query_count":0,
             "inputs":{"query_results":{"path":"run.query-results.json"},"dataset_payload_hash":"sha256:fixture"},
             "results":{"path":"run.metrics.jsonl"}}
            """);
        var run = EvalKitFormat.ReadRun(path);
        var result = EvalKitFormat.ReadResults(run);
        Assert.Equal(0.75, Assert.Single(Assert.Single(result.Queries).Results).Score);
        Assert.Equal(RunStatus.Completed, result.Status);
        File.AppendAllText(Path.Combine(root, "ranks.jsonl"), "\nchanged");
        Assert.Throws<InvalidDataException>(() => EvalKitFormat.ReadResults(run));
        envelope["inputs"]!["dataset_payload_hash"] = "sha256:other";
        File.WriteAllText(envelopePath, envelope.ToJsonString());
        Assert.Throws<InvalidDataException>(() => EvalKitFormat.ReadResults(run));
        File.Delete(envelopePath);
        Assert.Throws<FileNotFoundException>(() => EvalKitFormat.ReadResults(run));
    }

    [Fact]
    public async Task Psapi_bridge_preserves_image_ids_payload_integrity_and_original_unknown_safety()
    {
        var image = Path.Combine(root, "image.png");
        await File.WriteAllBytesAsync(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZusAAAAASUVORK5CYII="));
        var rows = Path.Combine(root, "captions.jsonl");
        await File.WriteAllTextAsync(rows, """{"caption":"a tiny image","image":"image.png"}""");
        var mapping = new TableMapping { Kind = TableKind.Documents, Format = TableFormat.JsonLines, Confirmed = true,
            Columns = new() { ["caption"] = ColumnRole.Text, ["image"] = ColumnRole.ImagePath } };
        var path = await DatasetImport.BuildAsync("Image fixture", EvalTask.ImageTextRetrieval,
            [new(rows, mapping, CaptionPairs: true)], 10, Path.Combine(root, "native"), default);
        var source = DatasetLoader.Load(path);
        Assert.Empty(PsapiJob.CheckDataset(PsapiProvider.Find("genoa")!, source));
        var bridge = await PsapiDatasetBridge.CreateAsync(source, Path.Combine(root, "bridge"), default);
        var snapshot = EvalKitFormat.LoadDataset(bridge.ManifestPath);
        Assert.Equal(source.Queries.Select(q => (q.Id, q.Text)), snapshot.Queries.Select(q => (q.Id, q.Text)));
        Assert.Equal(source.Documents.Select(d => d.Id), snapshot.Documents.Select(d => d.Id));
        Assert.Equal(await File.ReadAllBytesAsync(source.Documents[0].ImagePath!), await File.ReadAllBytesAsync(snapshot.Documents[0].ImagePath!));
        Assert.False(source.HasSafetyJudgments);
        Assert.Null(Assert.Single(source.Judgments).Harmful);
        Assert.Equal(source.ToReference().ContentHash, bridge.MatchResult(EvalKitDatasetIdentity.From(snapshot.ToReference())).ContentHash);
        Assert.Throws<InvalidDataException>(() => bridge.MatchResult(bridge.ExecutionIdentity with { PayloadHash = "sha256:wrong" }));
        var repeated = await PsapiDatasetBridge.CreateAsync(source, Path.Combine(root, "bridge2"), default);
        Assert.Equal(bridge.ExecutionIdentity, repeated.ExecutionIdentity);
        await File.AppendAllTextAsync(Path.Combine(root, "bridge", "queries.jsonl"), "\nchanged");
        Assert.Throws<InvalidDataException>(() => EvalKitFormat.LoadDataset(bridge.ManifestPath));

        source.Queries[0] = source.Queries[0] with { ImagePath = image };
        Assert.Contains(PsapiJob.CheckDataset(PsapiProvider.Find("genoa")!, source), e => e.Contains("text queries"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PsapiDatasetBridge.CreateAsync(source, Path.Combine(root, "unsupported"), default));
    }

    [Fact]
    public async Task Psapi_bridge_preserves_text_and_rejects_stale_native_content()
    {
        var source = DatasetLoader.Load(Fixtures.HelpdeskManifest);
        var bridge = await PsapiDatasetBridge.CreateAsync(source, Path.Combine(root, "text"), default);
        var snapshot = EvalKitFormat.LoadDataset(bridge.ManifestPath);
        Assert.Equal(source.Documents.Select(d => (d.Id, d.Text)), snapshot.Documents.Select(d => (d.Id, d.Text)));
        Assert.Equal(source.Queries.Count, snapshot.Queries.Count);
        var stale = new EvalDataset { Id = source.Id, Name = source.Name, SourcePath = source.SourcePath, ContentHash = "stale", Task = source.Task,
            Queries = source.Queries, Documents = source.Documents };
        await Assert.ThrowsAsync<InvalidDataException>(() => PsapiDatasetBridge.CreateAsync(stale, Path.Combine(root, "stale"), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PsapiDatasetBridge.CreateAsync(source, Path.Combine(root, "cancelled"), cancelled.Token));
        Assert.False(Directory.Exists(Path.Combine(root, "cancelled")));
    }

    [Fact]
    public async Task StarterPinsDownloadsNormalizesAndReusesVerifiedCache()
    {
        var handler = new FakeHandler(_ => Reply(Metadata().ToJsonString()), _ => Reply(Csv));
        using var http = new HttpClient(handler);
        var hub = new HuggingFaceDatasets(http, new NetworkPolicy(), Path.Combine(root, "cache"));
        var plan = await PublicDatasetStarters.InspectAsync(hub, Recipe, 1000, default);
        Assert.Single(handler.Requests);
        var path = await PublicDatasetStarters.ImportAsync(hub, plan, 1000, true, Path.Combine(root, "normalized"), null, default);
        var data = DatasetLoader.Load(path);
        Assert.Equal(2, data.Examples.Count);
        Assert.Equal(DatasetSource.HuggingFace, data.Provenance!.Source);
        Assert.Equal(Revision, data.Provenance.Revision);
        Assert.Contains("split=test", Assert.Single(data.Provenance.Components));
        Assert.DoesNotContain(data.ValidationIssues, x => x.Severity == ValidationSeverity.Error);
        Assert.Contains("/resolve/" + Revision + "/test.csv", handler.Requests[1].RequestUri!.AbsoluteUri);
        await PublicDatasetStarters.ImportAsync(hub, plan, 1000, true, Path.Combine(root, "cached-import"), null, default);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("unknown", false, 1000)]
    [InlineData("cc0-1.0", true, 1000)]
    [InlineData("cc0-1.0", false, 1)]
    public async Task StarterRejectsRestrictedOrOversizeInputsBeforeDownloading(string license, bool gated, long budget)
    {
        var handler = new FakeHandler(_ => Reply(Metadata(license, gated).ToJsonString()));
        using var http = new HttpClient(handler);
        var hub = new HuggingFaceDatasets(http, new NetworkPolicy(), root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PublicDatasetStarters.InspectAsync(hub, Recipe, budget, default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConsentAndIntegrityAreRequiredBeforeAStarterBecomesUsable()
    {
        var handler = new FakeHandler(_ => Reply(Metadata().ToJsonString()), _ => Reply(Csv.Replace("kitten", "puppyy")));
        using var http = new HttpClient(handler);
        var hub = new HuggingFaceDatasets(http, new NetworkPolicy(), Path.Combine(root, "cache"));
        var plan = await PublicDatasetStarters.InspectAsync(hub, Recipe, 1000, default);
        var target = Path.Combine(root, "normalized");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PublicDatasetStarters.ImportAsync(hub, plan, 1000, false, target, null, default));
        Assert.Single(handler.Requests);
        await Assert.ThrowsAsync<InvalidDataException>(() => PublicDatasetStarters.ImportAsync(hub, plan, 1000, true, target, null, default));
        Assert.False(File.Exists(Path.Combine(target, "dataset.smds.json")));
    }

    [Fact]
    public async Task ChangedSchemaDoesNotReceiveGuessedMappings()
    {
        var handler = new FakeHandler(_ => Reply(Metadata().ToJsonString()), _ => Reply(Csv));
        using var http = new HttpClient(handler);
        var hub = new HuggingFaceDatasets(http, new NetworkPolicy(), Path.Combine(root, "cache"));
        var recipe = Recipe with { Inputs = [Recipe.Inputs[0] with { Columns = new Dictionary<string, ColumnRole> { ["missing"] = ColumnRole.Text } }] };
        var plan = await PublicDatasetStarters.InspectAsync(hub, recipe, 1000, default);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PublicDatasetStarters.ImportAsync(hub, plan, 1000, true, Path.Combine(root, "normalized"), null, default));
        Assert.Contains("schema changed", ex.Message);
    }

    [Fact]
    public void CatalogAcceptsNormalCliDiagnosticsButRejectsStaleOrUnknownOutput()
    {
        var container = EvalKitDatasetContainer.Known.First();
        var entries = EvalKitDatasetCatalog.Parse("\u001b[2m[0:14]\u001b[0m \u001b[36m[CLI]\u001b[0m \u001b[32m[info]\u001b[0m Using catalog.json index\n" + """
            [0:14] [CLI] [info] Using catalog.json index (280 entries, verified just now)
              "Image Embedding RAI 001 (English)" v1.0.3 RAI queries
            [0:14] [CLI] [info] Found 1 dataset version(s)
            """, container);
        Assert.Equal("Image Embedding RAI 001 (English)", Assert.Single(entries).Name);
        Assert.Throws<InvalidOperationException>(() => EvalKitDatasetCatalog.Parse("[stale] Showing cached catalog", container));
        Assert.Throws<InvalidDataException>(() => EvalKitDatasetCatalog.Parse("[0:14] [CLI] [error] Access denied", container));
        Assert.Throws<InvalidDataException>(() => EvalKitDatasetCatalog.Parse("Unrecognized table format", container));
    }

    [Fact]
    public void NewEvaluationFetchPinsVersionButSavedRunFetchStillRequiresHash()
    {
        var container = EvalKitDatasetContainer.Known.First();
        var args = EvalKitCli.CatalogFetchArguments(new("registered", "v1.2.3", "", container), "account");
        Assert.Contains("1.2.3", args);
        Assert.Contains(container.Name, args);
        Assert.DoesNotContain("--expected-payload-hash", args);
        Assert.Throws<InvalidOperationException>(() => EvalKitCli.FetchArguments(new("registered", "1.2.3", null), "account", container.Name));
    }

    [Fact]
    public void FetchedManifestMustMatchVersionHashAndSelectedCache()
    {
        var path = Path.Combine(root, "manifest.json");
        File.WriteAllText(path, """{"description":{"name":"registered","version":"1.2.3"},"payload_hash":"sha256:abc"}""");
        Assert.Equal(path, EvalKitCli.ValidateFetchedManifest(path + "\n", root, new("registered", "v1.2.3", null)));
        Assert.Throws<InvalidDataException>(() => EvalKitCli.ValidateFetchedManifest(path, root, new("different", "1.2.3", null)));
        Assert.Throws<InvalidDataException>(() => EvalKitCli.ValidateFetchedManifest(path, root, new("registered", "1.2.4", null)));
        Assert.Throws<InvalidDataException>(() => EvalKitCli.ValidateFetchedManifest(path, root, new("registered", "1.2.3", "sha256:other")));
        Assert.Throws<InvalidDataException>(() => EvalKitCli.ValidateFetchedManifest(path, Path.Combine(root, "different-cache"), new("registered", "1.2.3", null)));
        Assert.Throws<InvalidDataException>(() => EvalKitCli.ValidateFetchedManifest(path + "\nextra output", root, new("registered", "1.2.3", null)));
    }

    [Theory]
    [InlineData("Image Embedding RAI 001 (English)")]
    [InlineData("Web Images Personal")]
    public void InventoryAliasesDoNotBypassApprovalPolicy(string name)
    {
        var provenance = DatasetPolicy.EvalKit(name, "Microsoft internal non-sensitive");
        Assert.Throws<InvalidOperationException>(() => DatasetPolicy.Ensure(provenance, DatasetOperation.Evaluate));
        Assert.Throws<InvalidOperationException>(() => DatasetPolicy.Ensure(provenance, DatasetOperation.Preview));
    }

    public void Dispose() => Directory.Delete(root, true);
}
