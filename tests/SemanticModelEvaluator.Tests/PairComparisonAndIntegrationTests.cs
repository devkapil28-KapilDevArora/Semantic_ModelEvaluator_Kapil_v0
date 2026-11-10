using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.EvalKit;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Export;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Pairing;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.Azure;
using SemanticModelEvaluator.Providers.EvalKit;
using Xunit;

namespace SemanticModelEvaluator.Tests;

/// <summary>Fake HTTP endpoint: records requests and replies from a queue. Never touches the network.</summary>
public sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
{
    private int next;
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        return replies[Math.Min(next++, replies.Length - 1)](request);
    }

    public static HttpResponseMessage Embeddings(int count, int dims = 3, int? tokens = 7) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["data"] = new JsonArray([.. Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
            {
                ["index"] = i, ["embedding"] = new JsonArray([.. Enumerable.Range(0, dims).Select(d => (JsonNode)(float)(i + d + 1))])
            })]),
            ["usage"] = tokens is { } t ? new JsonObject { ["prompt_tokens"] = t } : null
        }.ToJsonString())
    };
}

public sealed class FakeTokens : IAzureTokenSource
{
    public int Calls;
    public Task<string> GetTokenAsync(string scope, CancellationToken ct) { Calls++; return Task.FromResult("fake-entra-token-0123456789"); }
    public TokenCredential? Credential => null;
}

/// <summary>In-memory EvalKit results container.</summary>
public sealed class FakeContainer(Dictionary<string, byte[]> blobs, bool deny = false) : IResultContainer
{
    public string Description => "fakeaccount/fake-container";
    private void Check() { if (deny) throw new AccessDeniedException("Access denied to fakeaccount/fake-container. No other identity or container is tried."); }
#pragma warning disable CS1998
    public async IAsyncEnumerable<string> ListNamesAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Check();
        foreach (var k in blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order()) yield return k;
    }
    public async IAsyncEnumerable<string> ListPrefixesAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Check();
        foreach (var p in blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => prefix + k[prefix.Length..].Split('/')[0] + "/").Distinct()) yield return p;
    }
#pragma warning restore CS1998
    public Task<byte[]> ReadAsync(string blobName, CancellationToken ct) { Check(); return Task.FromResult(blobs[blobName]); }
    public Task DownloadToAsync(string blobName, string path, CancellationToken ct) { Check(); File.WriteAllBytes(path, blobs[blobName]); return Task.CompletedTask; }
}

/// <summary>Synthetic EvalKit files mirroring the documented schema (metrics envelope v3, query-results JSONL, dataset manifest).</summary>
public static class EvalKitFixtures
{
    public const string Hash = "sha256:0000000000000000000000000000000000000000000000000000000000000abc";

    public static string MetricsJson(string runId, string version = "1.0", string? hash = Hash, int schema = 3) => new JsonObject
    {
        ["schema_version"] = schema,
        ["run_id"] = runId,
        ["description"] = new JsonObject { ["name"] = runId, ["created_at"] = "2025-01-02T03:04:05Z" },
        ["inputs"] = new JsonObject { ["dataset"] = "fx-ek", ["dataset_version"] = version, ["dataset_payload_hash"] = hash },
        ["evalkit"] = new JsonObject { ["version"] = "2.1.0", ["config"] = new JsonObject { ["backend"] = "psapi", ["backend_version"] = "7", ["max_results_per_query"] = 3 } },
        ["query_count"] = 3, ["judged_count"] = 2, ["zero_result_count"] = 1, ["failed_query_count"] = 0, ["query_duration_seconds"] = 0.3,
        ["metric_names"] = new JsonArray("ndcg"),
        ["results"] = new JsonObject { ["path"] = runId + ".query-results.jsonl" }
    }.ToJsonString();

    public static string Results(params (string Q, string D, int Pos, double? Score)[] rows) =>
        string.Join("\n", rows.Select(r => new JsonObject { ["query_id"] = r.Q, ["document_id"] = r.D, ["position"] = r.Pos, ["score"] = r.Score }.ToJsonString()));

    public static string WriteRun(string folder, string runId, string results, string version = "1.0", string? hash = Hash)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, runId + ".metrics.json"), MetricsJson(runId, version, hash));
        File.WriteAllText(Path.Combine(folder, runId + ".query-results.jsonl"), results);
        return Path.Combine(folder, runId + ".metrics.json");
    }

    /// <summary>q1: d1 relevant (2), d2 harmful (−1), d3 unjudged (no relevance). q2: d3 relevant. q3: no judgments.</summary>
    public static string WriteDataset(string folder, string version = "1.0", string hash = Hash)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "queries.jsonl"), "{\"id\":\"q1\",\"text\":\"reset password\"}\n{\"id\":\"q2\",\"text\":\"vpn\"}\n{\"id\":\"q3\",\"text\":\"printer\"}");
        File.WriteAllText(Path.Combine(folder, "corpus.jsonl"), string.Join("\n", Enumerable.Range(1, 4).Select(i => $"{{\"id\":\"d{i}\",\"text\":\"doc {i}\"}}")));
        File.WriteAllText(Path.Combine(folder, "judgements.jsonl"),
            "{\"query_id\":\"q1\",\"doc_id\":\"d1\",\"relevance\":2}\n{\"query_id\":\"q1\",\"doc_id\":\"d2\",\"relevance\":-1}\n" +
            "{\"query_id\":\"q1\",\"doc_id\":\"d3\"}\n{\"query_id\":\"q2\",\"document_id\":\"d3\",\"relevance\":1}");
        var manifest = Path.Combine(folder, "fx-ek.manifest.json");
        File.WriteAllText(manifest, new JsonObject
        {
            ["description"] = new JsonObject { ["name"] = "fx-ek", ["version"] = version },
            ["payload_hash"] = hash,
            ["queries"] = new JsonObject { ["path"] = "queries.jsonl" },
            ["corpus"] = new JsonArray(new JsonObject { ["path"] = "corpus.jsonl" }),
            ["judgements"] = new JsonObject { ["path"] = "judgements.jsonl" }
        }.ToJsonString());
        return manifest;
    }

    public static PairSide Side(EvalKitRun ek, ModelRunResult r, double? threshold = null) => new()
    {
        Configuration = new ModelConfiguration(ek.Identity.ProviderId, ek.ProviderLabel, ek.Identity.ModelId, ek.ModelLabel, ek.Identity.Revision, null,
            new SortedDictionary<string, string>(), ek.Identity.ContentHash),
        Result = r,
        RunDataset = new DatasetReference(ek.Dataset.Name ?? "unknown", ek.Dataset.Name ?? "unknown", ek.Dataset.Version ?? "?", EvalTask.Retrieval, null, ek.Dataset.PayloadHash ?? ""),
        Origin = ResultOrigin.LoadedEvalKitLocal, RunId = ek.RunId, CachedDepth = ek.MaxResultsPerQuery, Threshold = threshold
    };
}

public class ProviderIntegrationTests
{
    private static FoundryDeployment Deployment(string auth = "EntraId", double? price = null) => new()
    {
        Name = "embed-small", Endpoint = "https://contoso.openai.azure.com", Deployment = "text-embedding-3-small",
        ModelVersion = "text-embedding-3-small/1", Auth = auth, PricePer1KInputTokens = price
    };

    private static FoundryRunner Runner(FakeHandler h, FoundryDeployment d, ISecretStore? secrets = null, FakeTokens? tokens = null) =>
        new(FoundryProvider.Describe(d), d, new HttpClient(h), new NetworkPolicy(), tokens ?? new FakeTokens(), secrets ?? new InMemorySecretStore())
        { Delay = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task Foundry_embeds_with_entra_token_and_reports_usage_without_cost_unless_configured()
    {
        var h = new FakeHandler(_ => FakeHandler.Embeddings(2));
        var tokens = new FakeTokens();
        await using var r = Runner(h, Deployment(), tokens: tokens);
        var v = await r.EmbedAsync(["a", "b"], EmbeddingRole.Document, CancellationToken.None);
        Assert.Equal(2, v.Length); Assert.Equal(3, v[0].Length);
        Assert.Equal("https://contoso.openai.azure.com/openai/v1/embeddings", h.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer", h.Requests[0].Headers.Authorization!.Scheme);
        Assert.Contains("\"model\":\"text-embedding-3-small\"", h.Bodies[0]);
        Assert.Equal(1, tokens.Calls);
        Assert.Equal(7, r.Usage.InputTokens);
        Assert.Null(r.Usage.EstimatedCost);
    }

    [Fact]
    public async Task Foundry_cost_is_only_estimated_from_an_explicit_price()
    {
        var h = new FakeHandler(_ => FakeHandler.Embeddings(1, tokens: 2000));
        await using var r = Runner(h, Deployment(price: 0.02));
        await r.EmbedAsync(["a"], EmbeddingRole.Query, CancellationToken.None);
        Assert.Equal(0.04, r.Usage.EstimatedCost!.Value, 6);
        Assert.Contains("not a billing record", r.Usage.CostBasis);
    }

    [Fact]
    public async Task Foundry_retries_transient_429_but_never_retries_403()
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => FakeHandler.Embeddings(1));
        await using (var r = Runner(h, Deployment()))
        {
            await r.EmbedAsync(["a"], EmbeddingRole.Query, CancellationToken.None);
            Assert.Equal(1, r.Usage.Retries); Assert.Equal(2, h.Requests.Count);
        }
        var denied = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await using var r2 = Runner(denied, Deployment());
        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => r2.EmbedAsync(["a"], EmbeddingRole.Query, CancellationToken.None));
        Assert.Single(denied.Requests);
        Assert.Equal(0, r2.Usage.Retries);
        Assert.Contains("Correlation id", ex.Message);
    }

    [Fact]
    public async Task Foundry_api_key_comes_only_from_secret_store_and_is_never_in_the_url()
    {
        var secrets = new InMemorySecretStore();
        var d = Deployment("ApiKey");
        await using (var missing = Runner(new FakeHandler(_ => FakeHandler.Embeddings(1)), d, secrets))
            await Assert.ThrowsAsync<AccessDeniedException>(() => missing.EmbedAsync(["a"], EmbeddingRole.Query, CancellationToken.None));
        secrets.Write(d.KeyName, "super-secret-key-value");
        var h = new FakeHandler(_ => FakeHandler.Embeddings(1));
        await using var r = Runner(h, d, secrets);
        await r.EmbedAsync(["a"], EmbeddingRole.Query, CancellationToken.None);
        Assert.Equal("super-secret-key-value", h.Requests[0].Headers.GetValues("api-key").Single());
        Assert.DoesNotContain("super-secret", h.Requests[0].RequestUri!.ToString());
        Assert.Null(h.Requests[0].Headers.Authorization);
    }

    [Theory]
    [InlineData("http://contoso.openai.azure.com")]
    [InlineData("https://contoso.openai.azure.com/?api-key=abc")]
    [InlineData("https://example.com")]
    public void Foundry_rejects_unsafe_endpoints(string endpoint)
    {
        var d = Deployment(); d.Endpoint = endpoint;
        Assert.False(FoundryProvider.ValidateConfiguration(d).IsValid);
        Assert.Equal(ReadinessState.Error, FoundryProvider.Describe(d).Readiness);
    }

    [Fact]
    public void Providers_normalize_to_shared_configuration_and_keep_provider_metadata_separate()
    {
        var foundry = ModelConfiguration.From(FoundryProvider.Describe(Deployment()));
        Assert.Equal(FoundryProvider.ProviderId, foundry.ProviderId);
        Assert.Equal("text-embedding-3-small/1", foundry.Revision);
        Assert.Equal("https://contoso.openai.azure.com", foundry.Endpoint);
        Assert.Equal("text-embedding-3-small", foundry.Parameters["deployment"]);
        Assert.DoesNotContain(foundry.Parameters, p => p.Key.Contains("key", StringComparison.OrdinalIgnoreCase));

        var hf = ModelConfiguration.From(new ModelDescriptor
        {
            Identity = new ModelIdentity(HuggingFaceProvider.ProviderId, "sentence-transformers/all-MiniLM-L6-v2", "abc123"),
            DisplayName = "all-MiniLM-L6-v2", Properties = new() { ["pooling"] = "mean", ["unrelated"] = "x" }
        });
        Assert.Equal("abc123", hf.Revision);
        Assert.Equal("mean", hf.Parameters["pooling"]);
        Assert.False(hf.Parameters.ContainsKey("unrelated"));

        var psapi = ModelConfiguration.From(PsapiProvider.Describe("ranker-x", "12"));
        Assert.Equal("psapi-12", psapi.Revision);
    }

    [Fact]
    public async Task Psapi_discovers_the_four_catalog_models_and_captured_runs()
    {
        var folder = Fixtures.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "result-manifest.json"), "{\"model\":\"ranker-x\",\"psapi_version\":\"12\"}");
        var p = new PsapiProvider(() => [(folder, "bundle")]);
        var found = await p.DiscoverAsync(new ModelQuery(), CancellationToken.None);
        Assert.Equal(["florence", "genoa", "palermo", "ranker-x", "spacev6"], found.Select(m => m.Identity.ModelId).Order());
        Assert.False(p.UsesNetwork);

        var captured = found.Single(m => m.Identity.ModelId == "ranker-x");
        Assert.Equal(ReadinessState.ImportOnly, captured.Readiness);
        Assert.Equal(folder, captured.LocalPath);
        Assert.True((await p.ValidateAsync(captured, CancellationToken.None)).IsValid);

        // Without a built backend the catalog models are listed honestly as not installed and validation explains what is missing.
        var genoa = found.Single(m => m.Identity.ModelId == "genoa");
        Assert.Equal(ReadinessState.NotInstalled, genoa.Readiness);
        Assert.Equal(ExecutionKind.ExternalProcess, genoa.Execution);
        var v = await p.ValidateAsync(genoa, CancellationToken.None);
        Assert.False(v.IsValid);
        Assert.Contains("EvalKit CLI was not found", v.ToString());
        await Assert.ThrowsAsync<NotSupportedException>(() => p.LoadAsync(genoa, new RuntimeOptions(), CancellationToken.None));
    }

    [Fact]
    public void Psapi_catalog_matches_the_authoritative_backend_contract()
    {
        var specs = PsapiProvider.Catalog.ToDictionary(s => s.ModelId);
        Assert.Equal((PsapiCorpus.Text, 100, 512), (specs["spacev6"].Corpus, specs["spacev6"].Dimensions, specs["spacev6"].MaxInputTokens));
        Assert.Equal((PsapiCorpus.Text, 768, 512), (specs["palermo"].Corpus, specs["palermo"].Dimensions, specs["palermo"].MaxInputTokens));
        Assert.Equal((PsapiCorpus.Image, 768, 77), (specs["florence"].Corpus, specs["florence"].Dimensions, specs["florence"].MaxInputTokens));
        Assert.Equal((PsapiCorpus.Image, 1024, 96), (specs["genoa"].Corpus, specs["genoa"].Dimensions, specs["genoa"].MaxInputTokens));
        Assert.Equal(["Florence", "Genoa", "Palermo", "Space v6"], PsapiProvider.Catalog.Select(s => s.DisplayName).Order());

        var status = new PsapiBackendStatus("cli.dll", @"C:\repo", @"C:\repo\bin", new Dictionary<string, string> { ["EmbeddingsLookup-Genoa"] = "1.0.521" }, "develop", "abc");
        var d = PsapiProvider.Describe(specs["genoa"], status);
        Assert.Equal(ReadinessState.Ready, d.Readiness);
        Assert.Equal("EmbeddingsLookup-Genoa@1.0.521", d.Identity.Revision);
        var cfg = ModelConfiguration.From(d);
        Assert.Equal(("image", "1024", "96"), (cfg.Parameters["corpus_modality"], cfg.Parameters["dimensions"], cfg.Parameters["max_input_tokens"]));
        Assert.DoesNotContain(cfg.Parameters, p => p.Key.Contains("key", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("token", StringComparison.OrdinalIgnoreCase) && p.Key != "max_input_tokens");
        Assert.Equal("package version unknown", PsapiProvider.Revision(specs["spacev6"], status));
    }

    [Fact]
    public void Psapi_backend_status_explains_each_missing_prerequisite()
    {
        var none = new Dictionary<string, string>();
        Assert.Contains("EvalKit CLI was not found", new PsapiBackendStatus(null, null, null, none, null, null).Summary);
        Assert.Contains("checkout", new PsapiBackendStatus("cli", null, null, none, null, null).Summary);
        var notBuilt = new PsapiBackendStatus("cli", @"C:\repo", null, none, null, null);
        Assert.False(notBuilt.CanRun);
        Assert.Contains("build.ps1", notBuilt.Summary);
        Assert.Contains("v145", notBuilt.Summary);
        Assert.True(new PsapiBackendStatus("cli", @"C:\repo", @"C:\repo\bin", none, "develop", "0123456789abcdef").CanRun);
    }

    [Fact]
    public async Task Psapi_contract_failures_block_readiness_and_validation()
    {
        const string error = "Unsupported PSAPI contract.";
        var status = new PsapiBackendStatus("cli", @"C:\repo", @"C:\repo\bin", new Dictionary<string, string>(), "develop", "abc")
        {
            ContractError = error
        };
        var provider = new PsapiProvider(() => [], () => status);
        var model = PsapiProvider.Describe(PsapiProvider.Find("spacev6")!, status);

        Assert.False(status.CanRun);
        Assert.Contains(error, status.Summary);
        Assert.Equal(ReadinessState.NotInstalled, model.Readiness);
        Assert.Contains(error, model.ReadinessDetail);
        var validation = await provider.ValidateAsync(model, CancellationToken.None);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains(error));
    }

    [Fact]
    public void Psapi_registry_preserves_build_identity_when_prerequisites_are_missing()
    {
        var root = Fixtures.NewTempDir();
        var build = new PsapiBuildRegistration
        {
            Id = "registered-build", Label = "Registered build",
            RepositoryRoot = root, CliPath = Path.Combine(root, "missing-cli.dll"),
            BackendDirectory = Path.Combine(root, "missing-backend")
        };

        var status = PsapiBuildRegistry.Inspect(build, CancellationToken.None);

        Assert.Equal(build.Id, status.BuildId);
        Assert.Equal(build.Label, status.BuildLabel);
        Assert.False(status.CanRun);
        Assert.NotNull(status.ContractError);
        Assert.Contains(status.ContractError, status.Summary);
        Assert.Null(status.Fingerprint);
        Assert.Null(status.RuntimeVersion);
        Assert.Empty(status.ModelIssues);
    }

    [Fact]
    public void Git_head_is_read_from_loose_and_packed_refs_without_running_git()
    {
        var root = Fixtures.NewTempDir();
        var git = Directory.CreateDirectory(Path.Combine(root, ".git")).FullName;
        Assert.Equal(((string?)null, (string?)null), PsapiBackendStatus.ReadGitHead(root));
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/develop\n");
        File.WriteAllText(Path.Combine(git, "packed-refs"), "# pack-refs\nfeedface refs/heads/develop\n");
        Assert.Equal(("develop", "feedface"), PsapiBackendStatus.ReadGitHead(root));
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        File.WriteAllText(Path.Combine(git, "refs", "heads", "develop"), "cafebabe\n");
        Assert.Equal(("develop", "cafebabe"), PsapiBackendStatus.ReadGitHead(root));
        File.WriteAllText(Path.Combine(git, "HEAD"), "0123abcd");
        Assert.Equal(((string?)null, (string?)"0123abcd"), PsapiBackendStatus.ReadGitHead(root));
    }

    [Fact]
    public void Psapi_job_uses_only_documented_parameters_and_absolute_paths()
    {
        var spec = PsapiProvider.Find("SpaceV6")!;
        var job = PsapiJob.Build(spec, "ds\\x.manifest.json", "pkg", 50, "SMEval_run");
        Assert.True(Path.IsPathFullyQualified(job["manifestPath"]!.GetValue<string>()));
        Assert.True(Path.IsPathFullyQualified(job["backend"]!["package"]!.GetValue<string>()));
        Assert.Equal("external", job["backend"]!["type"]!.GetValue<string>());
        var parameters = job["backend"]!["parameters"]!.AsObject();
        Assert.Equal(["modelId"], parameters.Select(p => p.Key));
        Assert.Equal("spacev6", parameters["modelId"]!.GetValue<string>());
        Assert.Equal(50, job["queryConfig"]!["maxResultsPerQuery"]!.GetValue<int>());
        Assert.False(job["failFast"]!.GetValue<bool>());
        Assert.Null(job["hostPrepare"]);
        Assert.Equal("filename", PsapiJob.Build(spec, "m", "p", 5, "n", "filename")["backend"]!["parameters"]!["textSource"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => PsapiJob.Build(PsapiProvider.Find("genoa")!, "m", "p", 5, "n", "filename"));
        Assert.Throws<ArgumentException>(() => PsapiJob.Build(spec, "m", "p", 5, "n", "url"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PsapiJob.Build(spec, "m", "p", 0, "n"));
        Assert.Matches("^SMEval_spacev6_My_data_v1_\\d{14}$", PsapiJob.RunName(spec, "My data/v1!", DateTimeOffset.UnixEpoch.AddYears(55)));
    }

    [Fact]
    public void Psapi_dataset_check_blocks_wrong_modality_and_unpersisted_sources()
    {
        var root = Fixtures.NewTempDir();
        var manifest = EvalKitFixtures.WriteDataset(Path.Combine(root, "ds"));
        var text = EvalKitFormat.LoadDataset(manifest, "Synthetic test data");
        Assert.Empty(PsapiJob.CheckDataset(PsapiProvider.Find("spacev6")!, text));
        Assert.Contains(PsapiJob.CheckDataset(PsapiProvider.Find("genoa")!, text), e => e.Contains("IMAGE corpus"));
        var images = new EvalDataset
        {
            Id = text.Id, Name = text.Name, Version = text.Version, Task = EvalTask.Retrieval, SourcePath = manifest, Queries = text.Queries,
            Documents = [new("i1", "", "a.jpg"), new("i2", "", "b.jpg")], Judgments = text.Judgments
        };
        Assert.Empty(PsapiJob.CheckDataset(PsapiProvider.Find("florence")!, images));
        Assert.Contains(PsapiJob.CheckDataset(PsapiProvider.Find("palermo")!, images), e => e.Contains("TEXT corpus"));
        Assert.Contains(PsapiJob.CheckDataset(PsapiProvider.Find("spacev6")!, Fixtures.Retrieval()), e => e.Contains("existing prepared dataset manifest"));
        Assert.Empty(PsapiJob.CheckDataset(PsapiProvider.Find("spacev6")!, DatasetLoader.Load(Fixtures.HelpdeskManifest)));
        Assert.Equal("inlineText", PsapiJob.TextSource(PsapiProvider.Find("spacev6")!, text));
    }

    [Fact]
    public void Evalkit_run_arguments_use_absolute_paths_and_force()
    {
        var args = EvalKitCli.RunArguments("job.json", "out");
        Assert.Equal("run", args[0]);
        Assert.True(Path.IsPathFullyQualified(args[1]));
        Assert.Equal("--output-dir", args[2]);
        Assert.True(Path.IsPathFullyQualified(args[3]));
        Assert.Equal("--force", args[4]);
    }

    [Fact]
    public void Backend_reported_model_identity_is_read_from_effective_config()
    {
        var root = Fixtures.NewTempDir();
        var path = EvalKitFixtures.WriteRun(root, "runP", EvalKitFixtures.Results(("q1", "d1", 1, 0.9)));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["evalkit"]!["effective_config"] = new JsonObject
        {
            ["parameters"] = new JsonObject { ["model_id"] = "palermo", ["model_version"] = "text: 2.0.0", ["dimension"] = 768, ["device"] = "CPU" }
        };
        json["host_machine_info"] = new JsonObject { ["machine_name"] = "SECRET-HOST" };
        File.WriteAllText(path, json.ToJsonString());
        var run = EvalKitFormat.ReadRun(path);
        Assert.Equal(("palermo", "text: 2.0.0", 768, "CPU"), (run.BackendModelId, run.BackendModelVersion, run.BackendDimension, run.BackendDevice));
        Assert.DoesNotContain("SECRET-HOST", run.BatchMetadata);
    }

    [Fact]
    public void Semantic_search_selector_excludes_generation_classification_and_reranking_models()
    {
        ModelDescriptor D(Modality output, params EvalTask[] tasks) => new()
        {
            Identity = new ModelIdentity(HuggingFaceProvider.ProviderId, "m", "1"), DisplayName = "m", Tasks = [.. tasks], OutputModality = output
        };
        Assert.True(HuggingFaceProvider.IsSemanticSearchModel(D(Modality.Embedding, EvalTask.Retrieval, EvalTask.SemanticSimilarity)));
        Assert.False(HuggingFaceProvider.IsSemanticSearchModel(D(Modality.Label, EvalTask.Generative)));
        Assert.False(HuggingFaceProvider.IsSemanticSearchModel(D(Modality.Label, EvalTask.Classification)));
        Assert.False(HuggingFaceProvider.IsSemanticSearchModel(D(Modality.Label, EvalTask.Reranking)));
        Assert.False(HuggingFaceProvider.IsSemanticSearchModel(D(Modality.Embedding)));
    }
}

public class PairSelectionTests
{
    private static ModelConfiguration Cfg(string provider, string model, string? rev = "1", string? endpoint = null, Dictionary<string, string>? p = null) =>
        new(provider, provider, model, model, rev, endpoint, new SortedDictionary<string, string>(p ?? []));

    [Theory]
    [InlineData("foundry", "huggingface")]
    [InlineData("psapi", "foundry")]
    [InlineData("huggingface", "psapi")]
    public void Any_cross_provider_pair_is_allowed(string a, string b)
    {
        var r = PairSelection.Validate(Cfg(a, "m1"), Cfg(b, "m2"));
        Assert.True(r.IsValid);
        Assert.Contains("Cross-provider", r.ToString());
    }

    [Fact]
    public void Two_distinct_models_from_the_same_provider_are_allowed()
        => Assert.True(PairSelection.Validate(Cfg("foundry", "m1"), Cfg("foundry", "m2")).IsValid);

    [Fact]
    public void Identical_configurations_are_rejected_but_variants_are_allowed()
    {
        Assert.False(PairSelection.Validate(Cfg("hf", "m"), Cfg("HF", "M")).IsValid);
        Assert.False(PairSelection.Validate(Cfg("hf", "m"), null).IsValid);
        var rev = PairSelection.Validate(Cfg("hf", "m", "1"), Cfg("hf", "m", "2"));
        Assert.True(rev.IsValid); Assert.Contains("version/revision", rev.ToString());
        Assert.True(PairSelection.Validate(Cfg("foundry", "m", endpoint: "https://a.openai.azure.com"), Cfg("foundry", "m", endpoint: "https://b.openai.azure.com")).IsValid);
        Assert.True(PairSelection.Validate(Cfg("hf", "m", p: new() { ["pooling"] = "mean" }), Cfg("hf", "m", p: new() { ["pooling"] = "cls" })).IsValid);
    }
}

public class EvalKitAndPairComparisonTests
{
    private static readonly PairComparer Comparer = new(new MetricRegistry());

    private static (EvalDataset Ds, PairSide A, PairSide B) LocalPair(string root, string versionB = "1.0", string? hashB = EvalKitFixtures.Hash)
    {
        var ds = EvalKitFormat.LoadDataset(EvalKitFixtures.WriteDataset(Path.Combine(root, "ds")), "Synthetic test data");
        var ra = EvalKitFormat.ReadRun(EvalKitFixtures.WriteRun(Path.Combine(root, "a"), "runA",
            EvalKitFixtures.Results(("q1", "d1", 1, 0.9), ("q1", "d2", 2, 0.4), ("q2", "d3", 1, 0.8), ("q3", "d4", 1, 0.2))));
        var rb = EvalKitFormat.ReadRun(EvalKitFixtures.WriteRun(Path.Combine(root, "b"), "runB",
            EvalKitFixtures.Results(("q1", "d2", 1, 0.95), ("q1", "d1", 2, 0.5)), versionB, hashB));
        var resB = EvalKitFormat.ReadResults(rb);
        var withB = new ModelRunResult { Model = new ModelIdentity("evalkit", "ranker-b", "7", "runB"), DisplayName = "ranker-b", Status = resB.Status, Queries = resB.Queries, Execution = resB.Execution, Performance = resB.Performance };
        return (ds, EvalKitFixtures.Side(ra, EvalKitFormat.ReadResults(ra)), EvalKitFixtures.Side(rb, withB) is var s ? new PairSide
        {
            Configuration = s.Configuration with { ModelId = "ranker-b" }, Result = withB, RunDataset = s.RunDataset, Origin = s.Origin, CachedDepth = s.CachedDepth
        } : null!);
    }

    [Fact]
    public void Local_evalkit_run_and_dataset_parse_with_identity_scores_and_unjudged_items()
    {
        var root = Fixtures.NewTempDir();
        var (ds, a, _) = LocalPair(root);
        Assert.Equal("fx-ek", ds.Name); Assert.Equal("1.0", ds.Version); Assert.Equal(EvalKitFixtures.Hash, ds.ContentHash);
        Assert.Null(ds.Judgments.Single(j => j.DocumentId == "d3" && j.QueryId == "q1").Grade);
        Assert.True(ds.Judgments.Single(j => j.DocumentId == "d2").Harmful);
        var ek = EvalKitFormat.ReadRun(Path.Combine(root, "a", "runA.metrics.json"));
        Assert.Equal(("psapi", "7", "2.1.0", 3), (ek.Backend, ek.BackendVersion, ek.EvalKitVersion, ek.MaxResultsPerQuery));
        Assert.Equal((3, 2, 1), (ek.QueryCount, ek.JudgedCount, ek.ZeroResultCount));
        Assert.Equal(("fx-ek", "1.0", EvalKitFixtures.Hash), (ek.Dataset.Name, ek.Dataset.Version, ek.Dataset.PayloadHash));
        Assert.Equal(0.9, a.Result.Queries.Single(q => q.QueryId == "q1").Results[0].Score);
        Assert.Equal(ExecutionKind.ImportedPredictions, a.Result.Execution);
        Assert.False(a.Result.Performance.Measured);
    }

    [Fact]
    public void Old_schema_and_incomplete_run_folders_are_rejected_with_actionable_errors()
    {
        var root = Fixtures.NewTempDir();
        File.WriteAllText(Path.Combine(root, "old.metrics.json"), EvalKitFixtures.MetricsJson("old", schema: 2));
        Assert.Contains("schema", Assert.Throws<InvalidDataException>(() => EvalKitFormat.ReadRun(Path.Combine(root, "old.metrics.json"))).Message);
        var other = Fixtures.NewTempDir();
        File.WriteAllText(Path.Combine(other, "x.metrics.json"), EvalKitFixtures.MetricsJson("x"));
        var run = EvalKitFormat.ReadRun(Path.Combine(other, "x.metrics.json"));
        Assert.Contains("Incomplete run", Assert.Throws<FileNotFoundException>(() => EvalKitFormat.ReadResults(run)).Message);
    }

    [Fact]
    public void Dataset_files_that_do_not_match_manifest_hashes_are_rejected()
    {
        var dir = Fixtures.NewTempDir();
        var manifest = EvalKitFixtures.WriteDataset(dir);
        var o = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        o["queries"]!["hash"] = "sha256:" + new string('0', 64);
        File.WriteAllText(manifest, o.ToJsonString());
        Assert.Throws<InvalidDataException>(() => EvalKitFormat.LoadDataset(manifest));
    }

    [Fact]
    public void Matching_runs_compare_like_for_like_from_captured_scores()
    {
        var (ds, a, b) = LocalPair(Fixtures.NewTempDir());
        var r = Comparer.Compare(ds, a, b, new PairSettings { K = 3 }, includeText: false);
        Assert.True(r.IsComparable, string.Join("; ", r.Blocking));
        Assert.Contains(r.Metrics, m => m.Definition.Id == "ndcg_at_k" && m.A?.Value is not null && m.B?.Value is not null);
        Assert.False(string.IsNullOrWhiteSpace(r.Recommendation.Headline));
        Assert.Contains(r.Limitations, l => l.Contains("recalculated by this application from captured EvalKit scores"));
        Assert.Contains(r.Limitations, l => l.Contains("do not establish that either model is better in general"));
    }

    [Theory]
    [InlineData("2.0", EvalKitFixtures.Hash, "version")]
    [InlineData("1.0", "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "hash")]
    public void Dataset_version_or_payload_mismatch_blocks_the_comparison(string version, string hash, string expected)
    {
        var (ds, a, b) = LocalPair(Fixtures.NewTempDir(), version, hash);
        var r = Comparer.Compare(ds, a, b, new PairSettings { K = 3 }, includeText: false);
        Assert.False(r.IsComparable);
        Assert.Contains(r.Blocking, x => x.StartsWith("Model B") && x.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.Blocking, x => x.StartsWith("Model A"));
        Assert.Contains("not", r.Recommendation.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(r.Metrics);
        Assert.Empty(r.Performance);
        Assert.Empty(r.Rows);
        Assert.Null(r.CurveA);
        Assert.Null(r.Agreement);
        Assert.Equal(CompatibilityStatus.Blocked, r.Compatibility.Single(c => c.Aspect == "Same judgments and inclusion rules").Status);
        var html = DetailedReportBuilder.Build(r, new DetailedReportOptions());
        Assert.Contains("Not evaluated: the selected runs are not comparable", html);
        Assert.DoesNotContain("<caption>Empty results and failures</caption>", html);
        Assert.DoesNotContain("<caption>Latency, errors and usage</caption>", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_common_in_scope_query_ids_blocks_even_when_dataset_identity_matches(bool unrelated)
    {
        var (ds, a, b) = LocalPair(Fixtures.NewTempDir());
        a.Result.Queries = [new QueryResult { QueryId = unrelated ? "other" : "q1" }];
        b.Result.Queries = [new QueryResult { QueryId = unrelated ? "other" : "q2" }];
        var r = Comparer.Compare(ds, a, b, new PairSettings { K = 3 }, false);
        Assert.False(r.IsComparable);
        Assert.Contains(r.Blocking, x => x.Contains("no common query/example IDs"));
        Assert.Equal(CompatibilityStatus.Blocked, r.Compatibility.Single(c => c.Aspect == "Same query set").Status);
        Assert.Empty(r.Metrics);
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Query_overlap_outside_the_selected_sample_does_not_make_runs_comparable()
    {
        var (ds, a, b) = LocalPair(Fixtures.NewTempDir());
        a.Result.Queries = [new QueryResult { QueryId = "q2" }];
        b.Result.Queries = [new QueryResult { QueryId = "q2" }];
        var r = Comparer.Compare(ds, a, b, new PairSettings { K = 3, SampledIds = ["q1"] }, false);
        Assert.False(r.IsComparable);
        Assert.Empty(r.Metrics);
        Assert.Equal(CompatibilityStatus.Blocked, r.Compatibility.Single(c => c.Aspect == "Same query set").Status);
    }

    [Fact]
    public void Unjudged_missing_and_failed_items_stay_unknown_and_are_counted_separately()
    {
        var ds = Fixtures.Retrieval();
        var a = Fixtures.RetrievalRunA();
        a.Queries.RemoveAll(q => q.QueryId == "q2");           // missing from run A
        var b = Fixtures.RetrievalRunB();
        b.Queries.Single(q => q.QueryId == "q1").Error = "timeout"; // partial provider failure
        var sideA = new PairSide { Configuration = ModelConfiguration.From(a, "test"), Result = a, RunDataset = ds.ToReference() };
        var sideB = new PairSide { Configuration = ModelConfiguration.From(b, "test"), Result = b, RunDataset = ds.ToReference() };
        var r = Comparer.Compare(ds, sideA, sideB, new PairSettings { K = 3 }, includeText: false);

        var q3 = r.Rows.Single(x => x.Id == "q3");
        Assert.True(q3.Flags.HasFlag(QueryFlags.Unjudged));
        Assert.Equal(QueryOutcome.Unknown, q3.Outcome);
        Assert.Null(q3.A.Quality);
        Assert.All(q3.A.ResultJudgments, j => Assert.StartsWith("unjudged", j));
        Assert.Equal(q3.A.Retained.Count, q3.A.ResultJudgments.Count);

        var q2 = r.Rows.Single(x => x.Id == "q2");
        Assert.True(q2.A.Missing); Assert.Equal(QueryOutcome.Unknown, q2.Outcome);
        var q1 = r.Rows.Single(x => x.Id == "q1");
        Assert.Equal("timeout", q1.B.Error); Assert.Equal(QueryOutcome.Unknown, q1.Outcome);

        Assert.Equal(1, r.CountsA.Missing);
        Assert.True(r.CountsB.Failed >= 1);
        Assert.True(r.CountsA.Unjudged >= 1);
        Assert.Equal(0, r.AWins + r.BWins); // nothing judged and complete on both sides
    }

    [Fact]
    public void Thresholds_are_per_side_and_recalculate_from_captured_scores_only()
    {
        var ds = Fixtures.Retrieval();
        var a = Fixtures.RetrievalRunA(); var b = Fixtures.RetrievalRunB();
        PairSide Side(ModelRunResult m, double? t) => new() { Configuration = ModelConfiguration.From(m, "test"), Result = m, RunDataset = ds.ToReference(), Threshold = t };
        var open = Comparer.Compare(ds, Side(a, null), Side(b, null), new PairSettings { K = 3 }, false);
        var strict = Comparer.Compare(ds, Side(a, 0.85), Side(b, null), new PairSettings { K = 3 }, false);
        Assert.True(strict.CountsA.Filtered > open.CountsA.Filtered);
        Assert.Equal(open.CountsB.Filtered, strict.CountsB.Filtered);
        Assert.True(strict.Rows.Single(r => r.Id == "q2").A.Retained.Count == 0); // 0.3 < 0.85 → empty
        Assert.Same(a, strict.A.Result); // the cached result object is reused, nothing re-run
        Assert.NotNull(strict.CurveA); Assert.NotNull(strict.CurveB);
    }

    [Fact]
    public void Unsupported_tasks_and_unknown_model_identity_are_reported()
    {
        var ds = Fixtures.Retrieval();
        var a = Fixtures.RetrievalRunA(); var b = Fixtures.RetrievalRunB();
        var sideA = new PairSide { Configuration = ModelConfiguration.From(a, "test") with { ModelId = EvalKitRun.UnknownModelPrefix + " (run 'x')" }, Result = a, RunDataset = ds.ToReference() };
        var sideB = new PairSide { Configuration = ModelConfiguration.From(b, "test"), Result = b, RunDataset = ds.ToReference() };
        var (_, warnings) = PairComparer.CheckEligibility(ds, sideA, sideB, 3);
        Assert.Contains(warnings, w => w.StartsWith("Model A") && w.Contains("identity is unknown"));
        Assert.DoesNotContain(warnings, w => w.StartsWith("Model B") && w.Contains("identity is unknown"));

        var similarity = new EvalDataset { Id = ds.Id, Name = ds.Name, Version = ds.Version, Task = EvalTask.SemanticSimilarity, Queries = ds.Queries, Documents = ds.Documents, Judgments = ds.Judgments };
        var (blocking, _) = PairComparer.CheckEligibility(similarity, sideA, sideB, 3);
        Assert.Contains(blocking, x => x.Contains("SemanticSimilarity") && x.Contains("workbench"));
    }

    [Fact]
    public void Dimensions_and_judged_only_inclusion_rule_recalculate_without_rerunning()
    {
        var ds = Fixtures.Retrieval();
        var a = Fixtures.RetrievalRunA(); var b = Fixtures.RetrievalRunB();
        PairSide Side(ModelRunResult m) => new() { Configuration = ModelConfiguration.From(m, "test"), Result = m, RunDataset = ds.ToReference() };
        var all = Comparer.Compare(ds, Side(a), Side(b), new PairSettings { K = 3 }, false);
        var judged = Comparer.Compare(ds, Side(a), Side(b), new PairSettings { K = 3, JudgedItemsOnly = true }, false);
        var judgedIds = PairComparer.JudgedIds(ds);
        Assert.True(judgedIds.Count < ds.Queries.Count);
        Assert.Equal(judgedIds.Count, judged.Rows.Count);
        Assert.DoesNotContain(judged.Rows, r => r.Flags.HasFlag(QueryFlags.Unjudged));
        Assert.Contains(judged.Limitations, l => l.Contains("judged items only"));
        Assert.True(all.Rows.Count > judged.Rows.Count);

        var harmOnly = Comparer.Compare(ds, Side(a), Side(b), new PairSettings { K = 3, Dimensions = [MetricCategory.Harm] }, false);
        Assert.All(harmOnly.Metrics, m => Assert.Equal(MetricCategory.Harm, m.Definition.Category));
        Assert.Contains(harmOnly.Limitations, l => l.Contains("dimensions limited to: Harm"));
        Assert.All(harmOnly.Rows, r => Assert.Equal(QueryOutcome.Unknown, r.Outcome));
        Assert.Null(harmOnly.PrimaryMetricId);

        var sampleIds = ds.Queries.Take(2).Select(q => q.Id).ToList();
        var sampled = Comparer.Compare(ds, Side(a), Side(b), new PairSettings { K = 3, SampledIds = sampleIds }, false);
        Assert.Equal(sampleIds, sampled.Rows.Select(r => r.Id));
        Assert.Equal(2, sampled.CountsA.Total);
    }

    [Fact]
    public void Failures_are_not_reported_as_disagreements()
    {
        var ds = Fixtures.Retrieval();
        var a = Fixtures.RetrievalRunA(); var b = Fixtures.RetrievalRunB();
        var qid = ds.Queries[0].Id;
        var ia = a.Queries.FindIndex(q => q.QueryId == qid);
        a.Queries[ia] = new QueryResult { QueryId = qid, Error = "HTTP 500" };
        b.Queries.RemoveAll(q => q.QueryId == qid);
        PairSide Side(ModelRunResult m) => new() { Configuration = ModelConfiguration.From(m, "test"), Result = m, RunDataset = ds.ToReference() };
        var row = Comparer.Compare(ds, Side(a), Side(b), new PairSettings { K = 3 }, false).Rows.Single(r => r.Id == qid);
        Assert.True(row.Flags.HasFlag(QueryFlags.Error));
        Assert.False(row.Flags.HasFlag(QueryFlags.Disagreement));
    }

    [Fact]
    public void Named_container_without_account_never_falls_back_to_default_store()
    {
        var id = new EvalKitDatasetIdentity("fx-ek", "1.0", EvalKitFixtures.Hash);
        Assert.Throws<InvalidOperationException>(() => EvalKitCli.FetchArguments(id, "", "eval-sensitive-published"));
        Assert.Throws<InvalidOperationException>(() => EvalKitCli.FetchArguments(id, null, "eval-sensitive-published"));
        Assert.Throws<InvalidOperationException>(() => EvalKitCli.FetchArguments(id, "scaievalkitdatasets", null));
        Assert.DoesNotContain("--container", EvalKitCli.FetchArguments(id));
    }

    [Fact]
    public void Dataset_containers_select_published_or_working_namespace_and_handling()
    {
        var id = new EvalKitDatasetIdentity("fx-ek", "1.0", EvalKitFixtures.Hash);
        var working = EvalKitDatasetContainer.Known.Single(c => c.Working && c.Sensitive);
        Assert.Equal("eval-sensitive-working", working.Name);
        Assert.Equal("evalkit/staging/datasets", EvalKitCli.FetchEnvironment(working)["EVALKIT_BLOB_PREFIX"]);
        Assert.Equal("1", EvalKitCli.FetchEnvironment(null)["EVALKIT_HEADLESS"]);
        Assert.False(EvalKitCli.FetchEnvironment(null).ContainsKey("EVALKIT_BLOB_PREFIX"));
        var args = EvalKitCli.FetchArguments(id, "scaievalkitdatasets", working.Name);
        Assert.Equal(["--storage-account", "scaievalkitdatasets", "--container", "eval-sensitive-working"], args.TakeLast(4));
        Assert.True(DetailedReportBuilder.IsSensitive(working.Classification));
        Assert.False(DetailedReportBuilder.IsSensitive(EvalKitDatasetContainer.Known.Single(c => !c.Working && !c.Sensitive).Classification));
        Assert.Equal("evalkit/published/datasets", EvalKitDatasetContainer.Known.Single(c => !c.Working && c.Sensitive).BlobPrefix);
    }

    [Fact]
    public void Run_batch_metadata_is_recorded_for_provenance()
    {
        var root = Fixtures.NewTempDir();
        EvalKitFixtures.WriteRun(root, "runA", EvalKitFixtures.Results(("q1", "d1", 1, 0.9)));
        var ek = EvalKitFormat.ReadRun(Path.Combine(root, "runA.metrics.json"));
        Assert.Contains("batch/experiment not recorded", ek.BatchMetadata);
        Assert.Contains("EvalKit 2.1.0", ek.BatchMetadata);
        Assert.Contains("backend psapi 7", ek.BatchMetadata);
        Assert.Contains("queries 3, judged 2, zero-result 1", ek.BatchMetadata);
        Assert.StartsWith(EvalKitRun.UnknownModelPrefix, ek.ModelLabel);
    }

    [Fact]
    public async Task Concurrency_note_does_not_mark_a_clean_run_as_failed()
    {
        using var root = new TempAppRoot();
        var engine = new EvaluationEngine(new ProviderRegistry([new DeterministicProvider()]));
        var run = await engine.RunAsync(Fixtures.Retrieval(), [new(DeterministicProvider.Describe("hash-word-256"))],
            new EvaluationConfig { K = 3, MaxConcurrency = 2 }, null, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, run.Models[0].Status);
        Assert.Contains(EvaluationEngine.ConcurrencyNote, run.Models[0].Warnings);
    }

    [Fact]
    public void Rank_only_results_are_flagged_so_thresholds_are_labelled_as_rank_cutoffs()
    {
        var dir = Fixtures.NewTempDir();
        var run = EvalKitFormat.ReadRun(EvalKitFixtures.WriteRun(dir, "r", EvalKitFixtures.Results(("q1", "d1", 1, null), ("q1", "d2", 2, null))));
        var res = EvalKitFormat.ReadResults(run);
        Assert.True(res.ScoresIncludeRanks);
        Assert.Equal([-1.0, -2.0], res.Queries.Single().Results.Select(d => d.Score));
        var scored = EvalKitFormat.ReadResults(EvalKitFormat.ReadRun(EvalKitFixtures.WriteRun(Fixtures.NewTempDir(), "s", EvalKitFixtures.Results(("q1", "d1", 1, 0.5)))));
        Assert.False(scored.ScoresIncludeRanks);
    }

    [Fact]
    public void Cancellation_stops_reading_captured_results()
    {
        var rows = Enumerable.Range(1, 3000).Select(i => ("q" + i, "d1", 1, (double?)0.5)).ToArray();
        var run = EvalKitFormat.ReadRun(EvalKitFixtures.WriteRun(Fixtures.NewTempDir(), "big", EvalKitFixtures.Results(rows)));
        Assert.Throws<OperationCanceledException>(() => EvalKitFormat.ReadResults(run, ct: new CancellationToken(true)));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void Only_item_specific_failures_are_retried_per_item(HttpStatusCode code, bool expected)
    {
        Assert.Equal(expected, EvaluationEngine.IsItemSpecific(new HttpRequestException("x", null, code)));
        Assert.False(EvaluationEngine.IsItemSpecific(new NotSupportedException()));
    }

    [Fact]
    public async Task Systematic_provider_failure_stops_instead_of_retrying_every_item()
    {
        using var root = new TempAppRoot();
        int calls = 0;
        var failing = new FakeProvider("f", id => new LambdaRunner(FakeProvider.Model("f", id), (_, _, _) =>
        { Interlocked.Increment(ref calls); throw new InvalidDataException("bad response"); }));
        var baseDs = Fixtures.Retrieval();
        var ds = new EvalDataset
        {
            Id = "many", Name = "many", Version = "1", Task = EvalTask.Retrieval, ContentHash = "m1", DataClassification = "Public synthetic",
            Queries = baseDs.Queries, Judgments = baseDs.Judgments,
            Documents = [.. Enumerable.Range(1, 40).Select(i => new DocumentItem("d" + i, "doc text " + i, null, new()))]
        };
        var engine = new EvaluationEngine(new ProviderRegistry([failing]));
        var run = await engine.RunAsync(ds, [new(FakeProvider.Model("f", "x"))], new EvaluationConfig { K = 3, BatchSize = 40 }, null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, run.Models[0].Status);
        Assert.Contains("consecutive item failures", run.Models[0].Error);
        Assert.Equal(1 + EvaluationEngine.MaxConsecutiveItemFailures, calls); // one batch call + capped per-item retries, not 40
    }

    [Fact]
    public void Queries_without_result_rows_are_empty_when_evalkit_recorded_no_failures()
    {
        var root = Fixtures.NewTempDir();
        // Envelope: 3 queries, zero_result_count 1, failed 0. Only q3 has no rows.
        var ek = EvalKitFormat.ReadRun(EvalKitFixtures.WriteRun(root, "runZ", EvalKitFixtures.Results(("q1", "d1", 1, 0.9), ("q2", "d3", 1, 0.8))));
        var withoutIds = EvalKitFormat.ReadResults(ek);
        Assert.DoesNotContain(withoutIds.Queries, q => q.QueryId == "q3");
        var r = EvalKitFormat.ReadResults(ek, expectedQueryIds: ["q1", "q2", "q3"]);
        var q3 = r.Queries.Single(q => q.QueryId == "q3");
        Assert.Empty(q3.Results); Assert.Null(q3.Error);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("dataset queries have no result rows"));
        // Inconsistent counts cannot establish which absent IDs were genuinely empty.
        var inconsistent = EvalKitFormat.ReadResults(ek, expectedQueryIds: ["q1", "q2", "q3", "q4"]);
        Assert.Equal(2, inconsistent.Queries.Count);
        Assert.Contains(inconsistent.Warnings, w => w.Contains("recorded 1 zero-result"));
        Assert.Contains("outside the selected dataset",
            Assert.Throws<InvalidDataException>(() => EvalKitFormat.ReadResults(ek, expectedQueryIds: ["other1", "other2", "other3"])).Message);
    }

    [Fact]
    public void Queries_without_result_rows_stay_unknown_when_failures_cannot_be_told_apart()
    {
        var root = Fixtures.NewTempDir();
        var metrics = EvalKitFixtures.WriteRun(root, "runF", EvalKitFixtures.Results(("q1", "d1", 1, 0.9)));
        File.WriteAllText(metrics, File.ReadAllText(metrics).Replace("\"failed_query_count\":0", "\"failed_query_count\":1"));
        var r = EvalKitFormat.ReadResults(EvalKitFormat.ReadRun(metrics), expectedQueryIds: ["q1", "q2", "q3"]);
        Assert.Single(r.Queries);
        Assert.Contains(r.Warnings, w => w.Contains("cannot be told apart"));
    }

    [Fact]
    public void Psapi_provenance_reaches_the_configuration_and_the_report()
    {
        var status = new PsapiBackendStatus("cli", "root", "pkg", new Dictionary<string, string>(), "develop", "feedface0123456789");
        var cfg = ModelConfiguration.From(PsapiProvider.Describe(PsapiProvider.Find("spacev6")!, status));
        Assert.Equal("feedface0123456789", cfg.Parameters["source_commit"]);
        Assert.Contains("psapi-embeddings-backend", cfg.Parameters["source"]);
        Assert.Equal("evalkit run (external backend, local CPU)", cfg.Parameters["invocation"]);
        var other = ModelConfiguration.From(PsapiProvider.Describe(PsapiProvider.Find("spacev6")!, status with { SourceCommit = "0000" }));
        Assert.NotEqual(cfg.Fingerprint, other.Fingerprint);
    }
}

public class EvalKitCloudTests
{
    private static (Dictionary<string, byte[]> Blobs, EvalKitCloudJob Job) Bundle(bool tamper = false)
    {
        var job = new EvalKitCloudJob("exp1", "12", "ranker-x", "fx-ek_v1", ResultSensitivity.NonSensitive);
        var metrics = Encoding.UTF8.GetBytes(EvalKitFixtures.MetricsJson("runX"));
        var results = Encoding.UTF8.GetBytes(EvalKitFixtures.Results(("q1", "d1", 1, 0.9)));
        string Sha(byte[] b) => "sha256:" + Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var manifest = new JsonObject
        {
            ["schema_version"] = 1, ["experiment"] = job.Experiment, ["blob_prefix"] = job.BlobPrefix, ["psapi_version"] = job.PsApiVersion,
            ["model"] = job.Model, ["dataset_key"] = job.DatasetKey,
            ["files"] = new JsonArray(
                new JsonObject { ["name"] = "runX.metrics.json", ["size_bytes"] = metrics.Length, ["sha256"] = Sha(metrics) },
                new JsonObject { ["name"] = "runX.query-results.jsonl", ["size_bytes"] = results.Length, ["sha256"] = Sha(results) })
        };
        if (tamper) results = Encoding.UTF8.GetBytes(EvalKitFixtures.Results(("q1", "d9", 1, 0.1)));
        return (new()
        {
            [job.BlobPrefix + "result-manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            [job.BlobPrefix + "runX.metrics.json"] = metrics,
            [job.BlobPrefix + "runX.query-results.jsonl"] = results
        }, job);
    }

    private static EvalKitCloudService Service(TempAppRoot root, IResultContainer c) =>
        new(new SettingsStore(Path.Combine(root.Root, "settings.json")), new NetworkPolicy(), _ => c);

    [Fact]
    public async Task Lists_jobs_and_downloads_one_hash_verified_bundle()
    {
        using var root = new TempAppRoot();
        var (blobs, job) = Bundle();
        var svc = Service(root, new FakeContainer(blobs));
        var jobs = await svc.ListJobsAsync("exp1", ResultSensitivity.NonSensitive, CancellationToken.None);
        Assert.Equal(job, Assert.Single(jobs));
        var folder = await svc.FetchAsync(job, null, CancellationToken.None);
        var run = EvalKitFormat.ReadRun(Assert.Single(EvalKitFormat.FindMetricsFiles(folder)), JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "result-manifest.json")))!.AsObject());
        Assert.Equal("psapi", run.Identity.ProviderId);
        Assert.Equal("psapi-12", run.Identity.Revision);
        Assert.Equal("ranker-x", run.Identity.ModelId);
        Assert.Contains(EvalKitCloudService.CachedBundles(), b => b.Folder == folder);
        EvalKitCloudService.VerifyBundle(folder);
    }

    [Fact]
    public async Task Cached_bundle_modified_after_download_fails_verification()
    {
        using var root = new TempAppRoot();
        var (blobs, job) = Bundle();
        var folder = await Service(root, new FakeContainer(blobs)).FetchAsync(job, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "runX.query-results.jsonl"), EvalKitFixtures.Results(("q1", "d9", 1, 0.99)));
        Assert.Throws<InvalidDataException>(() => EvalKitCloudService.VerifyBundle(folder));
    }

    [Fact]
    public async Task Tampered_bundles_are_discarded_and_identity_mismatch_is_rejected()
    {
        using var root = new TempAppRoot();
        var (blobs, job) = Bundle(tamper: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(root, new FakeContainer(blobs)).FetchAsync(job, null, CancellationToken.None));
        Assert.DoesNotContain(EvalKitCloudService.CachedBundles(), b => b.Folder.StartsWith(root.Root, StringComparison.OrdinalIgnoreCase));
        var (good, j2) = Bundle();
        var manifest = JsonNode.Parse(good[j2.BlobPrefix + "result-manifest.json"])!.AsObject();
        Assert.Throws<InvalidDataException>(() => EvalKitCloudService.ValidateManifest(manifest, j2 with { Model = "other" }));
    }

    [Fact]
    public async Task Sensitive_access_denial_surfaces_without_fallback()
    {
        using var root = new TempAppRoot();
        var asked = new List<ResultSensitivity>();
        var svc = new EvalKitCloudService(new SettingsStore(Path.Combine(root.Root, "settings.json")), new NetworkPolicy(),
            s => { asked.Add(s); return new FakeContainer([], deny: s == ResultSensitivity.Sensitive); });
        await Assert.ThrowsAsync<AccessDeniedException>(() => svc.ListJobsAsync("exp1", ResultSensitivity.Sensitive, CancellationToken.None));
        Assert.Equal([ResultSensitivity.Sensitive], asked);
        Assert.Equal("eval-sensitive-results", svc.Location(ResultSensitivity.Sensitive).Container);
        Assert.Equal("eval-nonsensitive-results", svc.Location(ResultSensitivity.NonSensitive).Container);
        Assert.Equal("scaievalkitdatasets", svc.Location(ResultSensitivity.NonSensitive).Account);
    }
}

public class DetailedReportTests
{
    private static readonly PairComparer Comparer = new(new MetricRegistry());

    private static EvalDataset Dataset(string classification, string queryText) => new()
    {
        Id = "fx", Name = "Fixture <b>retrieval</b>", Version = "1.0", Task = EvalTask.Retrieval, ContentHash = "fx1", DataClassification = classification,
        Queries = [new("q1", queryText), new("q2", "beta"), new("q3", "gamma")],
        Documents = Fixtures.Retrieval().Documents, Judgments = Fixtures.Retrieval().Judgments
    };

    private static PairComparisonResult Result(EvalDataset ds, bool includeText = true)
    {
        var a = Fixtures.RetrievalRunA(); a.DisplayName = "<script>alert('A')</script>";
        var b = Fixtures.RetrievalRunB();
        var cfgA = ModelConfiguration.From(a, "Microsoft AI Foundry") with
        {
            Endpoint = "https://contoso.openai.azure.com/?sig=SECRETSIG123&se=2030",
            Parameters = new SortedDictionary<string, string> { ["conn"] = "AccountKey=SECRETKEY123==;EndpointSuffix=core" }
        };
        return Comparer.Compare(ds,
            new PairSide { Configuration = cfgA, Result = a, RunDataset = ds.ToReference() },
            new PairSide { Configuration = ModelConfiguration.From(b, "Hugging Face"), Result = b, RunDataset = ds.ToReference() },
            new PairSettings { K = 3, Objective = "Pick a helpdesk retriever" }, includeText);
    }

    [Fact]
    public void Report_header_does_not_label_unknown_captured_sources_as_public()
    {
        var ds = Dataset("Public", "alpha");
        var pair = Result(ds);
        var side = new PairSide
        {
            Configuration = pair.A.Configuration, Result = pair.A.Result, RunDataset = pair.A.RunDataset,
            Classification = "Unknown classification (cached by another tool)"
        };
        var r = Comparer.Compare(ds, side, pair.B, new PairSettings { K = 3 }, false);
        var html = DetailedReportBuilder.Build(r, new DetailedReportOptions());
        var header = html[..html.IndexOf("</header>", StringComparison.Ordinal)];
        Assert.Contains("Sensitive or unclassified data", header);
        Assert.Contains("Unknown classification", header);
        Assert.Contains("Dataset: Public", header);
    }

    [Fact]
    public void Agreement_uses_ranks_only_and_matches_hand_computed_values()
    {
        var r = Result(Dataset("Public synthetic", "alpha"));
        var g = r.Agreement!;
        // q1: A {d2,d1,d4} vs B {d1,d3,d2} → 2/4; q2: {d5} vs {d3} → 0; q3: {d1} vs {} → 0.
        Assert.Equal(3, g.Queries);
        Assert.Equal(0.5 / 3, g.MeanTopKOverlap!.Value, 6);
        // Spearman: q1 ρ = 0, q2 ρ = −1, q3 skipped (union of one document).
        Assert.Equal(2, g.RankCorrelationQueries);
        Assert.Equal(-0.5, g.MeanRankCorrelation!.Value, 6);
        Assert.Equal((1, 3, 1, 0, 2), (g.RelevantA, g.RelevantB, g.RelevantBoth, g.RelevantMissedByB, g.RelevantMissedByA));
        Assert.False(r.RawScoresComparable); // different models: raw scores are never compared
    }

    [Fact]
    public void Map_at_k_is_reported_and_matches_hand_computed_values()
    {
        var r = Result(Dataset("Public synthetic", "alpha"));
        var map = r.Metrics.Single(m => m.Definition.Id == "map_at_k");
        // A: q1 relevant at rank 2 (d1) → (1/2)/min(2,3) = 0.25; q2 none → 0. Mean 0.125. B: q1 d1@1, d3@2 → (1+1)/2 = 1; q2 d3@1 → 1. Mean 1.
        Assert.Equal(0.125, map.A!.Value!.Value, 6);
        Assert.Equal(1.0, map.B!.Value!.Value, 6);
        Assert.Equal("B", map.Better);
    }

    [Fact]
    public void Latency_percentiles_stay_unknown_without_enough_samples()
    {
        Assert.Null(PairComparer.Percentile([1, 2, 3], 50, 5));
        Assert.Equal(3, PairComparer.Percentile([5, 1, 4, 2, 3], 50, 5));
        var many = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(95, PairComparer.Percentile(many, 95, 20));
        Assert.Equal(99, PairComparer.Percentile(many, 99, 100));
        var r = Result(Dataset("Public synthetic", "alpha"));
        Assert.All(r.Performance.Where(p => p.Name.StartsWith('P')), p => { Assert.Null(p.A); Assert.Null(p.B); });
    }

    [Fact]
    public void Compatibility_checks_cover_dataset_modality_scale_and_truncation()
    {
        var ds = Dataset("Public synthetic", "alpha");
        var a = Fixtures.RetrievalRunA(); var b = Fixtures.RetrievalRunB();
        var cfgA = ModelConfiguration.From(a, "PSAPI", new Dictionary<string, string> { ["corpus_modality"] = "text", ["max_input_tokens"] = "512", ["dimensions"] = "100" });
        var cfgB = ModelConfiguration.From(b, "PSAPI", new Dictionary<string, string> { ["corpus_modality"] = "image", ["max_input_tokens"] = "77", ["dimensions"] = "768" });
        var r = Comparer.Compare(ds,
            new PairSide { Configuration = cfgA, Result = a, RunDataset = ds.ToReference() },
            new PairSide { Configuration = cfgB, Result = b, RunDataset = ds.ToReference() }, new PairSettings { K = 3 }, includeText: false);
        CompatibilityStatus S(string aspect) => r.Compatibility.Single(c => c.Aspect == aspect).Status;
        Assert.Equal(CompatibilityStatus.Pass, S("Same dataset and exact version"));
        Assert.Equal(CompatibilityStatus.Warning, S("Modality"));
        Assert.Equal(CompatibilityStatus.Note, S("Score scale and direction"));
        Assert.Equal(CompatibilityStatus.Note, S("Truncation / input limit"));
        Assert.Equal(CompatibilityStatus.Unknown, S("Same candidate limit"));
        var html = DetailedReportBuilder.Build(r, new DetailedReportOptions());
        Assert.Contains("⚠ Warning", html);
        Assert.Contains("Mean top-K overlap", html);

        var mismatched = Comparer.Compare(ds,
            new PairSide { Configuration = cfgA, Result = a, RunDataset = ds.ToReference() with { Version = "2.0" } },
            new PairSide { Configuration = cfgB, Result = b, RunDataset = ds.ToReference() }, new PairSettings { K = 3 }, includeText: false);
        Assert.Equal(CompatibilityStatus.Blocked, mismatched.Compatibility.Single(c => c.Aspect == "Same dataset and exact version").Status);
        Assert.False(mismatched.IsComparable);
    }

    [Fact]
    public void Report_never_exports_endpoint_values_or_host_names()
    {
        var html = DetailedReportBuilder.Build(Result(Dataset("Public synthetic", "alpha")), new DetailedReportOptions());
        Assert.DoesNotContain("contoso.openai.azure.com", html);
        Assert.Contains("configured (value not exported)", html);
        Assert.DoesNotContain("machine_name", html);
    }

    [Fact]
    public void Report_is_self_contained_encoded_and_redacted()
    {
        var html = DetailedReportBuilder.Build(Result(Dataset("Public synthetic", "<img src=x onerror=alert(1)>")),
            new DetailedReportOptions { IncludeSampleContent = true, ApplicationVersion = "1.2.3+abc" });
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<b>retrieval</b>", html);
        Assert.DoesNotContain("SECRETSIG123", html);
        Assert.DoesNotContain("SECRETKEY123", html);
        Assert.DoesNotMatch("(src|href)\\s*=\\s*[\"']?https?://", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("1.2.3", html);
        Assert.Contains("<code>abc</code>", html);
        foreach (var section in new[] { "Executive summary", "Decision context", "Evaluation objective", "Model A profile", "Model B profile",
            "Source provenance", "configuration fingerprint", "Dataset and exact version", "Data-source provenance", "Evaluation and inclusion settings",
            "Compatibility assessment", "Summary metrics", "Retrieval-quality assessment", "Safety and harm assessment", "Empty-result and error analysis",
            "Judgment coverage and unjudged analysis", "Latency and reliability", "Threshold analysis", "Query-level differences", "Strengths and weaknesses",
            "Evidence-based recommendation", "Limitations", "Missing data", "Reproducibility metadata", "Generation timestamp", "Application version",
            "Source commit", "Report schema version" })
            Assert.Contains(section, html);
        for (int n = 1; n <= 28; n++) Assert.Contains($"id=\"s{n}\"", html);
        Assert.DoesNotContain("id=\"s29\"", html);
        Assert.Contains($"<code>{DetailedReportBuilder.SchemaVersion}</code>", html);
        Assert.Contains("Sort by", html); // sortable tables
        Assert.Contains("<details open><summary><h2", html); // collapsible sections
        Assert.Contains("Pick a helpdesk retriever", html);
    }

    [Fact]
    public void Report_values_match_the_comparison_and_unknowns_are_not_zero()
    {
        var r = Result(Dataset("Public synthetic", "alpha"));
        var html = DetailedReportBuilder.Build(r, new DetailedReportOptions());
        var ndcg = r.Metrics.Single(m => m.Definition.Id == "ndcg_at_k");
        Assert.Contains(PairComparer.Fmt(ndcg.A!.Value, ndcg.Definition.Format), html);
        Assert.Contains(PairComparer.Fmt(ndcg.B!.Value, ndcg.Definition.Format), html);
        Assert.Contains(PairComparer.Signed(ndcg.Delta, ndcg.Definition.Format), html);
        Assert.Contains("unknown", html); // q3 is unjudged: its quality is unknown, never 0
        Assert.Contains("role=\"img\"", html); // charts carry text alternatives
    }

    [Fact]
    public void Sample_content_from_sensitive_or_unclassified_data_requires_confirmation()
    {
        foreach (var classification in new[] { "Sensitive", "Unclassified", "" })
        {
            var r = Result(Dataset(classification, "private query text"));
            Assert.True(DetailedReportBuilder.IsSensitive(r));
            Assert.Throws<InvalidOperationException>(() => DetailedReportBuilder.Build(r, new DetailedReportOptions { IncludeSampleContent = true }));
            var confirmed = DetailedReportBuilder.Build(r, new DetailedReportOptions { IncludeSampleContent = true, SensitiveContentConfirmed = true });
            Assert.Contains("private query text", confirmed);
            var aggregate = DetailedReportBuilder.Build(Result(Dataset(classification, "private query text"), includeText: false), new DetailedReportOptions());
            Assert.DoesNotContain("private query text", aggregate);
            Assert.DoesNotContain("<tr data-keys", aggregate); // sensitive aggregate report has no per-item rows, ids or scores
            Assert.DoesNotContain("<code>q1</code>", aggregate);
        }
    }

    [Fact]
    public void Threshold_section_marks_active_thresholds()
    {
        var ds = Dataset("Public synthetic", "alpha");
        var a = Fixtures.RetrievalRunA(); var b = Fixtures.RetrievalRunB();
        var r = Comparer.Compare(ds,
            new PairSide { Configuration = ModelConfiguration.From(a, "Microsoft AI Foundry"), Result = a, RunDataset = ds.ToReference(), Threshold = 0.5 },
            new PairSide { Configuration = ModelConfiguration.From(b, "Hugging Face"), Result = b, RunDataset = ds.ToReference() },
            new PairSettings { K = 3 }, includeText: false);
        var html = DetailedReportBuilder.Build(r, new DetailedReportOptions());
        if (r.CurveA is not null) Assert.Contains("A threshold 0.5000", html);
    }

    [Fact]
    public void Sensitive_aggregate_report_masks_identifiers_quoted_in_warnings()
    {
        var r = Result(Dataset("Sensitive", "alpha"), includeText: false);
        r.Warnings.Add("Model B: query 'q-private-42' timed out.");
        var aggregate = DetailedReportBuilder.Build(r, new DetailedReportOptions());
        Assert.DoesNotContain("q-private-42", aggregate);
        Assert.Contains("'[item]'", WebUtility.HtmlDecode(aggregate));
        var pub = Result(Dataset("Public synthetic", "alpha"), includeText: false);
        pub.Warnings.Add("Model B: query 'q-public-7' timed out.");
        Assert.Contains("q-public-7", DetailedReportBuilder.Build(pub, new DetailedReportOptions()));
    }

    [Fact]
    public void Report_separates_export_time_from_comparison_time_and_handles_missing_commit()
    {
        var html = DetailedReportBuilder.Build(Result(Dataset("Public synthetic", "alpha")), new DetailedReportOptions { ApplicationVersion = "1.0.0" });
        Assert.Contains("Report generated:", html);
        Assert.Contains("Comparison computed:", html);
        Assert.Contains("Source commit: not recorded", html);
    }

    [Fact]
    public void Sensitive_aggregate_report_masks_unquoted_and_double_quoted_known_identifiers()
    {
        var r = Result(Dataset("Sensitive", "alpha"), includeText: false);
        r.Warnings.Add("Model B: query q2 failed; document \"d1\" missing.");
        var html = WebUtility.HtmlDecode(DetailedReportBuilder.Build(r, new DetailedReportOptions()));
        Assert.DoesNotContain("query q2 failed", html);
        Assert.DoesNotContain("\"d1\"", html);
        Assert.Contains("query [item] failed", html);
    }

    [Fact]
    public void Sensitive_aggregate_report_masks_identifiers_in_the_recommendation()
    {
        var r = Result(Dataset("Sensitive", "alpha"), includeText: false);
        typeof(PairComparisonResult).GetProperty(nameof(PairComparisonResult.Recommendation))!.SetValue(r, new PairRecommendation
        {
            Headline = "No recommendation.", Detail = "Blocked: query 'q-private-9' has no result.", Confidence = r.Recommendation.Confidence, ConfidenceReason = "document \"d-secret\" missing"
        });
        var html = WebUtility.HtmlDecode(DetailedReportBuilder.Build(r, new DetailedReportOptions()));
        Assert.DoesNotContain("q-private-9", html);
        Assert.DoesNotContain("d-secret", html);
    }

    [Fact]
    public void Report_suppresses_credential_named_parameters_and_connection_strings()
    {
        var r = Result(Dataset("Public synthetic", "alpha"));
        var cfg = r.A.Configuration with
        {
            Parameters = new SortedDictionary<string, string>
            {
                ["subscription-key"] = "SUBKEY0123456789", ["max_tokens"] = "256", ["target"] = "Endpoint=sb://x/;SharedAccessKeyName=n;SharedAccessKey=SAKEY999"
            }
        };
        var side = new PairSide { Configuration = cfg, Result = r.A.Result, RunDataset = r.A.RunDataset };
        var html = DetailedReportBuilder.Build(Comparer.Compare(Dataset("Public synthetic", "alpha"), side, r.B, r.Settings, true), new DetailedReportOptions());
        Assert.DoesNotContain("SUBKEY0123456789", html);
        Assert.DoesNotContain("SAKEY999", html);
        Assert.Contains("max_tokens=256", html);
    }

    [Fact]
    public void Svg_numbers_are_culture_invariant_and_threshold_table_lists_every_point()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var r = Result(Dataset("Public synthetic", "alpha"));
            var html = DetailedReportBuilder.Build(r, new DetailedReportOptions());
            Assert.DoesNotMatch("(x|y|width|x1|x2|y1|y2)=\"-?\\d+,\\d", html);
            Assert.DoesNotMatch("points=\"[^\"]*\\d,\\d+,", html);
            Assert.Equal("de-DE", System.Globalization.CultureInfo.CurrentCulture.Name);
            if (r.CurveA is { } ca && r.CurveMetricIds.Count > 0)
            {
                var section = html[html.IndexOf("id=\"s18\"", StringComparison.Ordinal)..html.IndexOf("id=\"s19\"", StringComparison.Ordinal)];
                var tables = System.Text.RegularExpressions.Regex.Matches(section, "<details>").Count;
                Assert.Equal(tables * (Math.Max(ca.Thresholds.Length, r.CurveB!.Thresholds.Length) + 1), System.Text.RegularExpressions.Regex.Matches(section, "<tr>").Count);
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abcDEF123/+==;EndpointSuffix=core.windows.net", "abcDEF123")]
    [InlineData("subscription-key=SUBK0123456789", "SUBK0123456789")]
    [InlineData("InstrumentationKey=1234abcd-0000;IngestionEndpoint=x", "1234abcd")]
    [InlineData("https://fn.azurewebsites.net/api/x?code=FUNCKEY123==", "FUNCKEY123")]
    [InlineData("client_secret: CS0123456789", "CS0123456789")]
    [InlineData("https://x.blob.core.windows.net/c?sv=2022&sig=Zm9vYmFy%3D&se=2030", "Zm9vYmFy")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghij", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("api-key: 0123456789abcdef", "0123456789abcdef")]
    public void Redactor_removes_storage_keys_sas_tokens_and_bearer_tokens(string input, string secret)
        => Assert.DoesNotContain(secret, Redactor.Redact(input));
}
