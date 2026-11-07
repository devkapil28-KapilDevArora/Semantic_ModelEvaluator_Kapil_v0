using System.Net;
using System.Text.Json.Nodes;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Comparison;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Export;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.Ffa;
using Xunit;

namespace SemanticModelEvaluator.Tests;

public class WorkspaceTests
{
    private static Workspace Build(EvalDataset ds, out EvaluationRun run)
    {
        var a = Fixtures.RetrievalRunA();
        var b = Fixtures.RetrievalRunB();
        run = new EvaluationRun { Dataset = ds.ToReference(), Task = ds.Task, Models = [a, b], Status = RunStatus.Completed };
        var w = new Workspace { Name = "ws", Dataset = ds.ToReference(), Runs = [run], Notes = "analyst note" };
        w.Models.Add(FakeProvider.Model("test", "model-a"));
        w.Scenarios.Add(new Scenario { Id = "s1", Name = "A @0.6", RunId = run.RunId, ModelKey = a.Model.Key, Threshold = 0.6, K = 3, Notes = "keep" });
        w.Scenarios.Add(new Scenario { Id = "s2", Name = "B", RunId = run.RunId, ModelKey = b.Model.Key, K = 3 });
        w.ReferenceScenarioId = "s2";
        w.View.Thresholds[a.Model.Key] = 0.6;
        w.View.SelectedMetricIds = ["ndcg_at_k", "precision_at_k"];
        w.View.CurveMetricId = "ndcg_at_k";
        w.View.ComparatorTab = "Heatmap";
        w.View.LinkThresholds = true;
        return w;
    }

    [Fact]
    public void Workspace_round_trips_scenarios_thresholds_reference_and_view_state()
    {
        var dir = Fixtures.NewTempDir();
        var ds = DatasetLoader.LoadManifest(Fixtures.HelpdeskManifest);
        var w = Build(ds, out var run);
        var path = Path.Combine(dir, "my");
        WorkspaceService.Save(w, path);
        Assert.True(File.Exists(path + AppPaths.WorkspaceExtension));

        var loaded = WorkspaceService.Open(path + AppPaths.WorkspaceExtension, id => id.ModelId == "model-a" ? FakeProvider.Model("test", "model-a") : null);
        var lw = loaded.Workspace;
        Assert.Equal("analyst note", lw.Notes);
        Assert.Equal("s2", lw.ReferenceScenarioId);
        Assert.Equal(0.6, lw.Scenarios.Single(s => s.Id == "s1").Threshold);
        Assert.Equal("keep", lw.Scenarios.Single(s => s.Id == "s1").Notes);
        Assert.Equal(0.6, lw.View.Thresholds[run.Models[0].Model.Key]);
        Assert.Equal("Heatmap", lw.View.ComparatorTab);
        Assert.True(lw.View.LinkThresholds);
        Assert.Equal(run.Models[0].Queries[0].Results.Select(r => r.Score), lw.Runs[0].Models[0].Queries[0].Results.Select(r => r.Score));
        Assert.NotNull(loaded.Dataset);
        Assert.Equal(ds.ContentHash, loaded.Dataset!.ContentHash);
    }

    [Fact]
    public void Opening_warns_about_missing_dataset_and_models_without_substituting()
    {
        var dir = Fixtures.NewTempDir();
        var copyDir = Path.Combine(dir, "data");
        Directory.CreateDirectory(copyDir);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Fixtures.HelpdeskManifest)!)) File.Copy(f, Path.Combine(copyDir, Path.GetFileName(f)));
        var ds = DatasetLoader.LoadManifest(Path.Combine(copyDir, "it-helpdesk.smds.json"));
        var w = Build(ds, out _);
        var path = Path.Combine(dir, "w.smeval");
        WorkspaceService.Save(w, path);

        Directory.Delete(copyDir, true);
        var loaded = WorkspaceService.Open(path, _ => null);
        Assert.Null(loaded.Dataset);
        Assert.Contains(loaded.Warnings, x => x.Message.Contains("ataset"));
        Assert.Contains(loaded.Warnings, x => x.Message.Contains("model-a"));
        Assert.Equal("test:model-a@1", loaded.Workspace.Models[0].Identity.Key); // identity preserved, not substituted
    }

    [Fact]
    public void Opening_warns_when_dataset_content_changed()
    {
        var dir = Fixtures.NewTempDir();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Fixtures.HelpdeskManifest)!)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var ds = DatasetLoader.LoadManifest(Path.Combine(dir, "it-helpdesk.smds.json"));
        var w = Build(ds, out _);
        var path = Path.Combine(dir, "w.smeval");
        WorkspaceService.Save(w, path);
        File.AppendAllText(Path.Combine(dir, "queries.csv"), "q99,new query\n");
        var loaded = WorkspaceService.Open(path, id => FakeProvider.Model(id.ProviderId, id.ModelId));
        Assert.Contains(loaded.Warnings, x => x.Message.Contains("changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_gets_new_name_and_keeps_scenarios()
    {
        var w = Build(Fixtures.Retrieval(), out _);
        var d = WorkspaceService.Duplicate(w, "copy");
        Assert.Equal("copy", d.Name);
        Assert.Equal(w.Scenarios.Count, d.Scenarios.Count);
        d.Scenarios[0].Name = "changed";
        Assert.NotEqual("changed", w.Scenarios[0].Name);
    }

    [Fact]
    public void Rejects_non_workspace_files()
    {
        var dir = Fixtures.NewTempDir();
        var p = Path.Combine(dir, "x.smeval");
        File.WriteAllText(p, "{\"format\":\"other\"}");
        Assert.ThrowsAny<Exception>(() => WorkspaceService.Open(p));
    }
}

public class ExportAndPrivacyTests
{
    private static ExportContext Context(out EvalDataset ds)
    {
        ds = Fixtures.Retrieval();
        var reg = new MetricRegistry();
        var te = new ThresholdEngine(reg);
        var a = Fixtures.RetrievalRunA();
        var b = Fixtures.RetrievalRunB();
        var sa = new Scenario { Id = "A", Name = "Scenario A", ModelKey = a.Model.Key, K = 3, Threshold = 0.5 };
        var sb = new Scenario { Id = "B", Name = "Scenario B", ModelKey = b.Model.Key, K = 3 };
        string[] ids = ["precision_at_k", "ndcg_at_k"];
        List<ScenarioEvaluation> evals = [new(sa, a, te.Evaluate(ds, a, 0.5, 3, ids)), new(sb, b, te.Evaluate(ds, b, null, 3, ids))];
        var w = new Workspace { Name = "export", Dataset = ds.ToReference(), Scenarios = [sa, sb], ReferenceScenarioId = "A" };
        var ds2 = new EvalDataset { Id = ds.Id, Name = ds.Name, Task = ds.Task, IsSynthetic = ds.IsSynthetic, Queries = [new("q1", "SECRET-QUERY-TEXT"), .. ds.Queries.Skip(1)], Documents = ds.Documents, Judgments = ds.Judgments, ContentHash = ds.ContentHash };
        ds = ds2;
        return new ExportContext
        {
            Workspace = w,
            Dataset = ds2,
            Evaluations = evals,
            Comparison = ComparisonEngine.Compare(evals, "A", ids.Select(i => reg.Find(i)!.Definition)),
        };
    }

    [Fact]
    public void Exports_include_identities_thresholds_reference_and_limitations()
    {
        var c = Context(out _);
        var csv = ReportExporter.ToCsv(c);
        Assert.Contains("precision_at_k", csv);
        Assert.Contains("test:model-a@1", csv);
        var json = JsonNode.Parse(ReportExporter.ToJson(c, new ExportOptions()))!;
        Assert.NotNull(json);
        var html = ReportExporter.ToHtml(c, new ExportOptions());
        Assert.Contains("Scenario A", html);
        Assert.Contains("0.5", html);
        Assert.Contains("eference", html);
        var text = ReportExporter.ToText(c);
        Assert.Contains("Limitations", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ReportExporter.StandardLimitations(c), l => l.Contains("ynthetic"));
    }

    [Fact]
    public void Example_text_is_excluded_from_exports_by_default()
    {
        var c = Context(out _);
        Assert.DoesNotContain("SECRET-QUERY-TEXT", ReportExporter.ToHtml(c, new ExportOptions()));
        Assert.DoesNotContain("SECRET-QUERY-TEXT", ReportExporter.ToJson(c, new ExportOptions()));
        Assert.DoesNotContain("SECRET-QUERY-TEXT", ReportExporter.ToCsv(c));
        Assert.DoesNotContain("SECRET-QUERY-TEXT", ReportExporter.ToText(c));
        Assert.Contains("SECRET-QUERY-TEXT", ReportExporter.ToHtml(c, new ExportOptions { IncludeExampleText = true }));
    }

    [Fact]
    public void Html_export_escapes_content()
    {
        var c = Context(out _);
        c.Workspace.Name = "<script>alert(1)</script>";
        Assert.DoesNotContain("<script>alert(1)</script>", ReportExporter.ToHtml(c, new ExportOptions()));
    }

    [Theory]
    [InlineData("token hf_abcdefghijklmnopqrstuvwxyz123456 used", "hf_abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("api_key=SuperSecretValue123", "SuperSecretValue123")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef1234", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef1234")]
    public void Redactor_removes_credentials(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redactor.Redact(input));
    }

    [Fact]
    public void Redactor_hides_user_profile_path()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(profile, Redactor.Redact($"file at {profile}\\x.txt"));
    }

    [Fact]
    public void Log_never_writes_tokens_to_disk()
    {
        var dir = Fixtures.NewTempDir();
        var log = new AppLog(dir);
        log.Info("test", "using hf_abcdefghijklmnopqrstuvwxyz123456");
        var content = string.Join("", Directory.GetFiles(dir).Select(File.ReadAllText));
        Assert.DoesNotContain("hf_abcdefghijklmnopqrstuvwxyz123456", content);
        Assert.DoesNotContain("hf_abcdefghijklmnopqrstuvwxyz123456", log.Entries[0].Message);
    }

    [Fact]
    public void Network_policy_blocks_offline_and_non_https()
    {
        var p = new NetworkPolicy { OfflineMode = true };
        Assert.Throws<InvalidOperationException>(() => p.EnsureAllowed("x", new Uri("https://huggingface.co")));
        p.OfflineMode = false;
        Assert.Throws<InvalidOperationException>(() => p.EnsureAllowed("x", new Uri("http://huggingface.co")));
        Uri? seen = null;
        p.NetworkAccess += (_, u) => seen = u;
        p.EnsureAllowed("x", new Uri("https://huggingface.co/api"));
        Assert.NotNull(seen);
    }
}

public sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

public class ProviderTests
{
    private static SettingsStore Settings() => new(Path.Combine(Fixtures.NewTempDir(), "settings.json"));

    [Fact]
    public async Task Registry_discovery_skips_network_providers_unless_allowed_or_cache_aware()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no network in this test"));
        var hf = new HuggingFaceProvider(new HttpClient(handler), new NetworkPolicy(), new InMemorySecretStore(), Settings(), Fixtures.NewTempDir());
        var reg = new ProviderRegistry([new DeterministicProvider(), hf, new FfaProvider(Settings(), registrationDirectory: Fixtures.NewTempDir())]);
        var (models, errors) = await reg.DiscoverAllAsync(new ModelQuery(AllowNetwork: false), CancellationToken.None);
        Assert.Empty(errors);
        Assert.Empty(handler.Requests);
        Assert.Contains(models, m => m.Identity.ModelId == "hash-word-256");
        Assert.Contains(models, m => m.Identity.ModelId.StartsWith("microsoft/") && m.Readiness == ReadinessState.NotInstalled);
    }

    [Fact]
    public async Task Missing_provider_reports_health_instead_of_throwing()
    {
        var ffa = new FfaProvider(Settings(), registrationDirectory: Path.Combine(Fixtures.NewTempDir(), "nope"));
        var h = await ffa.CheckHealthAsync(CancellationToken.None);
        Assert.False(h.Available);
        Assert.Equal("Not installed", h.Status);
        var reg = new ProviderRegistry([ffa]);
        Assert.Throws<MissingProviderException>(() => reg.Get("does-not-exist"));
    }

    [Fact]
    public void Deterministic_embeddings_are_stable_and_normalised()
    {
        var f = DeterministicProvider.Variants[0].Features;
        var a = DeterministicProvider.Embed("Reset my password", f);
        var b = DeterministicProvider.Embed("Reset my password", f);
        Assert.Equal(a, b);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => (double)x * x)), 5);
        Assert.True(EvaluationEngine.Dot(a, DeterministicProvider.Embed("password reset", f)) > EvaluationEngine.Dot(a, DeterministicProvider.Embed("coffee machine", f)));
    }

    [Fact]
    public void Hf_onnx_selection_and_prefixes()
    {
        Assert.Equal("onnx/model.onnx", HuggingFaceProvider.ChooseOnnx(["onnx/model.onnx", "onnx/model_qint8_avx512.onnx", "vocab.txt"], false).Onnx);
        Assert.Equal("onnx/model_qint8_avx512.onnx", HuggingFaceProvider.ChooseOnnx(["onnx/model.onnx", "onnx/model_qint8_avx512.onnx", "vocab.txt"], true).Onnx);
        Assert.NotNull(HuggingFaceProvider.ChooseOnnx(["model.safetensors", "vocab.txt"], false).Problem);
        Assert.NotNull(HuggingFaceProvider.ChooseOnnx(["onnx/model.onnx", "tokenizer.json"], false).Problem);
        Assert.Equal("query: ", HuggingFaceProvider.KnownPrefixes("intfloat/e5-small-v2").Query);
        Assert.StartsWith("Represent", HuggingFaceProvider.KnownPrefixes("BAAI/bge-small-en-v1.5").Query);
        Assert.Null(HuggingFaceProvider.KnownPrefixes("sentence-transformers/all-MiniLM-L6-v2").Query);
    }

    [Fact]
    public void Hf_describe_labels_unsupported_and_gated_honestly()
    {
        var hf = new HuggingFaceProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), new NetworkPolicy(), new InMemorySecretStore(), Settings(), Fixtures.NewTempDir());
        var gen = hf.Describe(JsonNode.Parse("""{"id":"org/llm","sha":"abc","pipeline_tag":"text-generation","siblings":[{"rfilename":"model.safetensors"}]}""")!);
        Assert.Equal(ReadinessState.ImportOnly, gen.Readiness);
        var gated = hf.Describe(JsonNode.Parse("""{"id":"org/emb","sha":"abc","gated":"manual","pipeline_tag":"feature-extraction","siblings":[{"rfilename":"onnx/model.onnx"},{"rfilename":"vocab.txt"}]}""")!);
        Assert.Equal(ReadinessState.RequiresAuthentication, gated.Readiness);
        Assert.True(gated.IsGated);
        var ok = hf.Describe(JsonNode.Parse("""{"id":"org/emb2","sha":"abc","gated":false,"pipeline_tag":"sentence-similarity","cardData":{"license":"mit"},"siblings":[{"rfilename":"onnx/model.onnx","size":100},{"rfilename":"vocab.txt","size":10}]}""")!);
        Assert.Equal(ReadinessState.NeedsDownload, ok.Readiness);
        Assert.Equal("mit", ok.License);
        Assert.Equal("abc", ok.Identity.Revision);
        Assert.Equal(110, ok.DownloadSizeBytes);
    }

    [Fact]
    public async Task Hf_respects_offline_mode_and_sends_token_only_to_huggingface()
    {
        var secrets = new InMemorySecretStore();
        secrets.Write(HuggingFaceProvider.TokenName, "hf_testtoken");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var policy = new NetworkPolicy { OfflineMode = true };
        var hf = new HuggingFaceProvider(new HttpClient(handler), policy, secrets, Settings(), Fixtures.NewTempDir());
        await Assert.ThrowsAsync<InvalidOperationException>(() => hf.SearchAsync("bge", CancellationToken.None));
        Assert.Empty(handler.Requests);
        var offline = await hf.DiscoverAsync(new ModelQuery("bge", AllowNetwork: true), CancellationToken.None);
        Assert.Empty(offline);
        Assert.Empty(handler.Requests);

        policy.OfflineMode = false;
        await hf.SearchAsync("bge", CancellationToken.None);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("huggingface.co", req.RequestUri!.Host);
        Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Local_onnx_provider_reports_unsupported_folders()
    {
        var dir = Fixtures.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), "{}");
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), [1, 2, 3]);
        var s = Settings();
        var p = new LocalOnnxProvider(s);
        p.Register(dir);
        var models = await p.DiscoverAsync(new ModelQuery(), CancellationToken.None);
        var m = Assert.Single(models);
        Assert.Equal(ReadinessState.Unsupported, m.Readiness);
        Assert.Contains("WordPiece", m.ReadinessDetail);
    }

    [Fact]
    public async Task Imported_predictions_identity_tracks_file_content()
    {
        var dir = Fixtures.NewTempDir();
        var f = Path.Combine(dir, "p.csv");
        File.WriteAllText(f, "query_id,doc_id,score\nq1,d1,1\n");
        var p = new ImportedPredictionsProvider(Settings());
        var m = p.Register(f, "My run", EvalTask.Retrieval);
        Assert.True((await p.ValidateAsync(m, CancellationToken.None)).IsValid);
        File.AppendAllText(f, "q2,d1,1\n");
        Assert.False((await p.ValidateAsync(m, CancellationToken.None)).IsValid);
    }

    [Fact]
    public void Ffa_registrations_are_validated()
    {
        var root = Fixtures.NewTempDir();
        var good = Path.Combine(root, "good");
        Directory.CreateDirectory(good);
        var exe = Path.Combine(root, "fake.exe");
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(good, "provider.json"), $$"""{"id":"microsoft","executable":{{System.Text.Json.JsonSerializer.Serialize(exe)}},"protocol_version":1}""");
        var bad = Path.Combine(root, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "provider.json"), """{"id":"Bad Id","executable":"relative.exe"}""");
        var (regs, problems) = FfaRegistry.Discover(root);
        Assert.Equal("microsoft", Assert.Single(regs).Id);
        Assert.Single(problems);
    }
}

/// <summary>In-memory FFA worker used to test the protocol client without launching processes.</summary>
public sealed class FakeChannel(Func<string, IEnumerable<string>> respond, IEnumerable<string>? startup = null) : IJsonLineChannel
{
    private readonly Queue<string> outbox = new(startup ?? []);
    public List<string> Sent { get; } = [];
    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        Sent.Add(line);
        foreach (var r in respond(line)) outbox.Enqueue(r);
        return Task.CompletedTask;
    }
    public Task<string?> ReadLineAsync(CancellationToken ct) => Task.FromResult(outbox.Count > 0 ? outbox.Dequeue() : null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class FfaProtocolTests
{
    private static IEnumerable<string> Worker(string line)
    {
        var req = JsonNode.Parse(line)!;
        var op = req["operation"]!.ToString();
        return op switch
        {
            "query" => ["{\"event\":\"progress\",\"message\":\"x\"}", "{\"ok\":true,\"result\":[0.6,0.8]}"],
            "documents" => [$"{{\"ok\":true,\"result\":[{string.Join(",", req["args"]!["texts"]!.AsArray().Select(_ => "[1,0]"))}]}}"],
            "shutdown" => ["{\"ok\":true,\"result\":null}"],
            "memory_ready" => [],
            _ => ["{\"ok\":false,\"error\":\"unknown\",\"code\":\"bad_op\",\"retryable\":false}"]
        };
    }

    [Fact]
    public async Task Worker_handshake_query_and_documents()
    {
        var channel = new FakeChannel(Worker, ["{\"event\":\"startup\",\"message\":\"loading\"}", "{\"event\":\"memory_request\",\"model_bytes\":10}", "{\"ok\":true,\"result\":\"ready\"}"]);
        var model = FfaProvider.DescribeBinding(new FfaWorkerBinding { Name = "w", WorkerExecutable = "C:\\nope.exe", ModelDirectory = "C:\\nope" });
        model.Readiness = ReadinessState.Ready;
        model.Execution = ExecutionKind.ExternalProcess;
        var provider = new FfaProvider(new SettingsStore(Path.Combine(Fixtures.NewTempDir(), "s.json")), registrationDirectory: Fixtures.NewTempDir(), channelFactory: (_, _) => channel);
        await using var runner = (ITextEmbeddingRunner)await provider.LoadAsync(model, new RuntimeOptions(), CancellationToken.None);
        Assert.Contains(channel.Sent, s => s.Contains("memory_ready"));
        var q = await runner.EmbedAsync(["hello"], EmbeddingRole.Query, CancellationToken.None);
        Assert.Equal([0.6f, 0.8f], q[0]);
        var d = await runner.EmbedAsync(["a", "b"], EmbeddingRole.Document, CancellationToken.None);
        Assert.Equal(2, d.Length);
        Assert.Contains(channel.Sent, s => s.Contains("\"operation\":\"documents\"") && s.Contains("\"texts\""));
    }

    [Fact]
    public async Task Error_envelope_becomes_protocol_exception()
    {
        var channel = new FakeChannel(Worker);
        var ex = await Assert.ThrowsAsync<FfaProtocolException>(() => FfaProtocol.CallAsync(channel, "nope", null, null, CancellationToken.None));
        Assert.Equal("bad_op", ex.Code);
    }

    [Fact]
    public void Catalog_is_mapped_without_claiming_readiness()
    {
        var catalog = JsonNode.Parse("""{"provider":"microsoft","version":"v1","models":[{"id":"palermo","label":"Palermo","kinds":["Document"],"state":"available"},{"id":"florence","label":"Florence","kinds":["Document","Image"],"state":"available"}],"internal_access_verified":false}""")!;
        var list = FfaProvider.DescribeCatalog(new FfaRegistration("microsoft", "C:\\x.exe", "v1", 1, ""), catalog).ToList();
        Assert.Equal(2, list.Count);
        Assert.All(list, m => Assert.NotEqual(ReadinessState.Ready, m.Readiness));
        Assert.Contains(EvalTask.ImageTextRetrieval, list[1].Tasks);
        Assert.All(list, m => Assert.Equal(FfaProvider.ProviderId, m.Identity.ProviderId));
    }
}
