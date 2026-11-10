using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SemanticModelEvaluator.App.Services;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Comparison;
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
using SemanticModelEvaluator.Providers.EvalKit;

namespace SemanticModelEvaluator.App.ViewModels;

public sealed record PairMetricView(string Name, string Category, string Direction, string A, string B, string Delta, string Better,
    double BarA, double BarB, bool HasBars, string Note);
public sealed record PairPerformanceView(string Name, string A, string B, string Delta, string Basis);
public sealed record PairCompatibilityView(string Aspect, string StatusText, string Detail);
public sealed record PairCountView(string Name, string A, string B);
public sealed record PairHeatCell(string Metric, string Text, string Background, string Tooltip);

/// <summary>
/// "Compare two models" workflow: 1 source → 2 models → 3 dataset → 4 settings → 5 run/load and review → 6 download HTML.
/// Loading a captured run, recalculating from captured scores and invoking a model are distinct, labelled actions.
/// </summary>
public sealed class PairComparisonViewModel : ObservableObject, IDisposable
{
    public const string ColorA = "#0072B2";
    public const string ColorB = "#D55E00";
    private const int BarWidth = 140;
    private readonly ProviderRegistry providers;
    private readonly EvaluationEngine engine;
    private readonly PairComparer comparer;
    private readonly IUserDialogs dialogs;
    private readonly SettingsStore settings;
    private readonly EvalKitCli cli;
    private readonly AppLog log;
    private readonly Dictionary<string, (ModelRunResult Result, DatasetReference Dataset, int Depth, DateTimeOffset At, string RunId)> evaluated = [];
    private CancellationTokenSource? operation;
    private CancellationTokenSource? recalculation;
    private EvalDataset? dataset;
    private (PairSide A, PairSide B, EvalDataset Dataset, PairSettings Settings)? basis;
    private PairComparisonResult? result;
    private bool busy;
    private string status = "Choose a source for each side. Captured EvalKit results are loaded without invoking a model.";
    private string pairValidation = "";
    private string datasetInfo = "No dataset loaded.";
    private string datasetMatch = "";
    private int k = 10;
    private int sampleLimit;
    private bool reuse = true;
    private string objective = "Decide which of two model configurations retrieves relevant results more reliably on this dataset, without increasing harm.";
    private bool includeSampleContent;
    private string rowFilter = "All";
    private PairQueryRow? selectedRow;
    private string? curveMetric;
    private List<ChartSeries> curvesA = [], curvesB = [];
    private RadarProjection? radar;
    private string radarNote = "";

    public PairComparisonViewModel(ProviderRegistry providers, EvaluationEngine engine, MetricRegistry metrics, IUserDialogs dialogs,
        SettingsStore settings, EvalKitCloudService cloud, EvalKitCli cli, AppLog log)
    {
        this.providers = providers; this.engine = engine; comparer = new PairComparer(metrics);
        this.dialogs = dialogs; this.settings = settings; this.cli = cli; this.log = log;
        A = new PairSideViewModel("Model A", ColorA, providers, cloud, dialogs, Command, s => Status = s, () => Token);
        B = new PairSideViewModel("Model B", ColorB, providers, cloud, dialogs, Command, s => Status = s, () => Token);
        foreach (var side in new[] { A, B }) { side.Changed += OnSelectionChanged; side.ThresholdChanged += ScheduleRecalculation; }
        OpenDatasetCommand = Command(OpenDatasetAsync);
        FindCachedDatasetCommand = Command(FindCachedDatasetAsync);
        FetchDatasetCommand = Command(FetchDatasetAsync);
        DemoCommand = Command(LoadDemoAsync);
        RunCommand = Command(RunAsync);
        UseLivePsapiCommand = Command(UseLivePsapiAsync);
        CancelCommand = new RelayCommand(() => operation?.Cancel());
        DownloadCommand = Command(DownloadAsync);
        OnSelectionChanged();
    }

    public PairSideViewModel A { get; }
    public PairSideViewModel B { get; }
    public IAsyncRelayCommand OpenDatasetCommand { get; }
    public IAsyncRelayCommand FindCachedDatasetCommand { get; }
    public IAsyncRelayCommand FetchDatasetCommand { get; }
    public IAsyncRelayCommand DemoCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IAsyncRelayCommand UseLivePsapiCommand { get; }
    public bool CanUseLivePsapi => new[] { A, B }.Any(s => s.IsCapturedSource
        && s.Configuration is { ProviderId: PsapiProvider.ProviderId } c && PsapiProvider.Find(c.ModelId) is not null);
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IReadOnlyList<SourceOption> SourceOptions => PairSideViewModel.SourceOptions;
    public IReadOnlyList<string> EligibilityRules => PairComparer.EligibilityRules;
    public static IReadOnlyList<string> RowFilters { get; } =
        ["All", "Model A wins", "Model B wins", "Ties", "Disagreements", "Relevant items missed", "Safety concerns", "Empty results", "Errors", "Unjudged", "Missing content", "Unknown outcome"];

    public bool Busy { get => busy; private set { if (SetProperty(ref busy, value)) OnPropertyChanged(nameof(NotBusy)); } }
    public bool NotBusy => !busy;
    public string Status { get => status; set => SetProperty(ref status, value); }
    public string PairValidation { get => pairValidation; private set => SetProperty(ref pairValidation, value); }
    public string DatasetInfo { get => datasetInfo; private set => SetProperty(ref datasetInfo, value); }
    public string DatasetMatch { get => datasetMatch; private set => SetProperty(ref datasetMatch, value); }
    public int K { get => k; set { if (SetProperty(ref k, Math.Clamp(value, 1, 1000))) { OnPropertyChanged(nameof(ModePlan)); ScheduleRecalculation(); } } }
    /// <summary>0 = all items. A sample keeps the dataset identity; unsampled items are out of scope, never counted as failures.</summary>
    public int SampleLimit
    {
        get => sampleLimit;
        set
        {
            if (!SetProperty(ref sampleLimit, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(ModePlan));
            if (result is not null) { InvalidateComparison(); Status = "Sample limit changed; the previous comparison no longer matches the selected scope. Run or load the comparison again."; }
        }
    }
    public bool ReuseExisting { get => reuse; set { if (SetProperty(ref reuse, value)) OnPropertyChanged(nameof(ModePlan)); } }
    public string Objective { get => objective; set { if (SetProperty(ref objective, value)) ScheduleRecalculation(); } }
    private bool includeQuality = true, includeHarm = true, includeCoverage = true, judgedOnly;
    // Evaluation dimensions and inclusion rules only change which captured results are summarised; no model is re-run.
    public bool IncludeQuality { get => includeQuality; set { if (SetProperty(ref includeQuality, value)) ScheduleRecalculation(); } }
    public bool IncludeHarm { get => includeHarm; set { if (SetProperty(ref includeHarm, value)) ScheduleRecalculation(); } }
    public bool IncludeCoverage { get => includeCoverage; set { if (SetProperty(ref includeCoverage, value)) ScheduleRecalculation(); } }
    public bool JudgedItemsOnly { get => judgedOnly; set { if (SetProperty(ref judgedOnly, value)) ScheduleRecalculation(); } }
    private PairSettings BuildSettings(IReadOnlyCollection<string>? sampledIds)
    {
        IReadOnlyCollection<MetricCategory>? dims = includeQuality && includeHarm && includeCoverage ? null
            : [.. new[] { (includeQuality, MetricCategory.Quality), (includeHarm, MetricCategory.Harm), (includeCoverage, MetricCategory.Coverage), (includeCoverage, MetricCategory.Distribution) }
                .Where(x => x.Item1).Select(x => x.Item2)];
        return new PairSettings { K = k, SampledIds = sampledIds, Objective = objective, Dimensions = dims, JudgedItemsOnly = judgedOnly };
    }
    public bool IncludeSampleContent { get => includeSampleContent; set => SetProperty(ref includeSampleContent, value); }
    public bool HasDataset => dataset is not null;
    public bool HasCapturedDataset => CapturedIdentity() is not null;

    public string ModePlan
    {
        get
        {
            string Plan(PairSideViewModel s)
            {
                if (s.IsCapturedSource) return $"{s.Title}: LOAD existing captured results ({s.Run?.SourceDescription ?? "select a run"}). No model is invoked.";
                if (s.Configuration is null) return $"{s.Title}: select a model.";
                var cached = dataset is not null && reuse && evaluated.ContainsKey(EvaluationKey(s.Configuration, dataset));
                var remote = s.Model?.Properties.GetValueOrDefault("remote_inference") == "true";
                return cached ? $"{s.Title}: REUSE this session's evaluation of the same configuration, dataset, K and sample. No model is invoked."
                    : $"{s.Title}: INVOKE {s.Configuration.ProviderName} for a NEW evaluation" + (remote ? " (dataset text is sent to the remote endpoint after confirmation)." : " (local).");
            }
            return Plan(A) + "\n" + Plan(B) + "\nChanging a threshold afterwards only RECALCULATES metrics from captured scores; it never re-runs or retrains a model.";
        }
    }

    // ---- results ---------------------------------------------------------------------------------------------
    public PairComparisonResult? Result { get => result; private set { if (SetProperty(ref result, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => result is not null;
    public string Headline { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string Blocking { get; private set; } = "";
    public string Warnings { get; private set; } = "";
    public string Strengths { get; private set; } = "";
    public string Limitations { get; private set; } = "";
    public List<PairCompatibilityView> CompatibilityRows { get; private set; } = [];
    public string AgreementText { get; private set; } = "";
    private HashSet<string> relevantQueries = new(StringComparer.Ordinal);
    public List<PairMetricView> MetricRows { get; private set; } = [];
    public List<PairPerformanceView> PerformanceRows { get; private set; } = [];
    public List<PairCountView> Counts { get; private set; } = [];
    public List<PairHeatCell> Heatmap { get; private set; } = [];
    public List<PairQueryRow> Rows { get; private set; } = [];
    public List<string> CurveMetrics { get; private set; } = [];
    public string? CurveMetric { get => curveMetric; set { if (SetProperty(ref curveMetric, value)) BuildCurves(); } }
    public List<ChartSeries> CurvesA { get => curvesA; private set => SetProperty(ref curvesA, value); }
    public List<ChartSeries> CurvesB { get => curvesB; private set => SetProperty(ref curvesB, value); }
    public RadarProjection? Radar { get => radar; private set => SetProperty(ref radar, value); }
    public string RadarNote { get => radarNote; private set => SetProperty(ref radarNote, value); }
    public string RowFilter { get => rowFilter; set { if (SetProperty(ref rowFilter, value)) ApplyRowFilter(); } }
    public PairQueryRow? SelectedRow { get => selectedRow; set { if (SetProperty(ref selectedRow, value)) OnPropertyChanged(nameof(RowDetail)); } }
    public string RowDetail
    {
        get
        {
            var r = selectedRow;
            if (r is null) return "Select a row to inspect both models' results.";
            string Side(string name, SideDetail s) =>
                $"{name}: {s.State}" + (s.Error is { } e ? $" — {Redactor.Redact(e)}" : "") +
                (s.Predicted is { } p ? $"\n  predicted {p}, score {PairComparer.Fmt(s.Score)}" : "") +
                (s.Retained.Count > 0 ? $"\n  retained {s.Retained.Count} (relevant {s.RelevantRetained}, harmful {s.HarmfulRetained}, unjudged {s.UnjudgedRetained}), filtered {s.Filtered}" +
                    "\n  top: " + string.Join(", ", s.Retained.Take(5).Select((d, i) => $"{d.DocumentId} ({d.Score:0.###}; {(i < s.ResultJudgments.Count ? s.ResultJudgments[i] : "unjudged")})")) : "") +
                $"\n  per-item quality {PairComparer.Fmt(s.Quality)}";
            return $"{r.Id} — {r.OutcomeText}{(r.FlagsText.Length > 0 ? " · " + r.FlagsText : "")}\n" +
                (r.Text is { Length: > 0 } t ? "Text: " + t + "\n" : "Text: not available in this dataset (or not loaded).\n") +
                (r.Label is { } l ? "Label: " + l + "\n" : "") +
                Side("A", r.A) + "\n" + Side("B", r.B) + (r.Explanation is { } x ? "\n" + x : "");
        }
    }

    private CancellationToken Token => operation?.Token ?? CancellationToken.None;

    private IAsyncRelayCommand Command(Func<Task> action) => new AsyncRelayCommand(async () =>
    {
        if (Busy) return;
        Busy = true;
        using var cts = new CancellationTokenSource();
        operation = cts;
        try { await action(); }
        catch (OperationCanceledException) { Status = "Cancelled. Completed results are preserved."; }
        catch (AccessDeniedException ex) { Status = "Access denied: " + Redactor.Redact(ex.Message) + "\nSign in with an authorized account (az login) or request access. No other identity or container was tried."; log.Warn("pair", "Access denied (details shown in UI)."); }
        catch (Exception ex) { Status = "Error: " + Redactor.Redact(ex.Message); log.Error("pair", ex.GetType().Name + ": operation failed; details shown in UI."); }
        finally { operation = null; Busy = false; OnPropertyChanged(nameof(ModePlan)); }
    });

    private void OnSelectionChanged()
    {
        InvalidateComparison();
        var v = PairSelection.Validate(A.Configuration, B.Configuration);
        PairValidation = string.Join("\n", v.Issues.Select(i => (i.Severity == ValidationSeverity.Error ? "✖ " : i.Severity == ValidationSeverity.Warning ? "⚠ " : "✔ ") + i.Message));
        UpdateDatasetMatch();
        OnPropertyChanged(nameof(HasCapturedDataset));
        OnPropertyChanged(nameof(CanUseLivePsapi));
        OnPropertyChanged(nameof(ModePlan));
    }

    private void InvalidateComparison()
    {
        recalculation?.Cancel();
        basis = null; Result = null; ClearResults();
    }

    private Task UseLivePsapiAsync()
    {
        var choices = new[] { A, B }.Where(s => s.IsCapturedSource && s.Configuration?.ProviderId == PsapiProvider.ProviderId)
            .Select(s => (Side: s, Spec: PsapiProvider.Find(s.Configuration!.ModelId))).Where(x => x.Spec is not null).ToList();
        if (choices.Count == 0) throw new InvalidOperationException("Select a captured Genoa, Florence, Palermo or Space v6 run first.");
        var backend = PsapiBackendStatus.Probe(cli);
        if (!backend.CanRun) throw new InvalidOperationException(backend.Summary);
        var selections = choices.Select(x => (x.Side, Model: PsapiProvider.Describe(x.Spec!, backend))).ToList();
        if (!dialogs.Confirm("Replace captured PSAPI results with installed local models?\n" +
            string.Join("\n", selections.Select(x => $"{x.Side.Title}: {x.Side.Configuration!.Label} -> {x.Model.DisplayName} ({x.Model.Identity.Revision})")) +
            "\nThe installed model/runtime may differ from the captured run. Thresholds will be reset. No inference starts now; use Run or load comparison to evaluate the dataset selected in Step 3.")) return Task.CompletedTask;
        foreach (var x in selections) x.Side.SelectProviderModel(PsapiProvider.ProviderId, x.Model);
        Status = "Live PSAPI models selected; previous thresholds and results cleared. Check Step 3, then Run or load comparison. Captured runs were not modified.";
        return Task.CompletedTask;
    }

    // ---- dataset ---------------------------------------------------------------------------------------------
    private async Task OpenDatasetAsync()
    {
        if (dialogs.Open("Dataset manifest (this app or EvalKit)|*.smds.json;*.manifest.json;*.json") is not { } path) return;
        await LoadDatasetAsync(path);
    }

    public async Task LoadDatasetAsync(string path, string? classification = null, CancellationToken cancellationToken = default, string? datasetState = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
        var ct = linked.Token;
        var ds = await Task.Run(() => path.EndsWith(".smds.json", StringComparison.OrdinalIgnoreCase)
            ? DatasetLoader.LoadManifest(path) : EvalKitFormat.LoadDataset(path, classification, ct), ct);
        if (ds.ValidationIssues.Any(i => i.Severity == ValidationSeverity.Error))
            throw new InvalidDataException(string.Join("\n", ds.ValidationIssues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Message)));
        ct.ThrowIfCancellationRequested();
        dataset = ds; InvalidateComparison();
        var judged = ds.Task is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking ? ds.JudgmentCoverage : ds.LabelCoverage;
        var withText = ds.Queries.Count(q => !string.IsNullOrWhiteSpace(q.Text)) + ds.Examples.Count(e => !string.IsNullOrWhiteSpace(e.Text));
        var cacheRoot = EvalKitFormat.DatasetCacheRoot(settings.Current.EvalKit.DatasetCacheDirectory);
        var cache = ds.SourcePath is { } sp && sp.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase) ? "EvalKit dataset cache (exact match verified by name, version, payload hash)"
            : ds.SourcePath is { } local ? "Local file: " + local : "unknown";
        DatasetInfo =
            $"{ds.Name}  ·  version {ds.Version}  ·  task {ds.Task}\n" +
            $"Content / payload hash: {ds.ContentHash}\n" +
            $"Handling classification: {ds.DataClassification}{(ds.IsSynthetic ? " (synthetic)" : "")}\n" +
            $"Source / cache: {cache}\n" +
            $"Dataset state: {datasetState ?? "not recorded (local file or cache; published vs working is only known when fetched from a named container)"}\n" +
            $"Items: {ds.RecordCount} · documents {ds.Documents.Count} · judgment/label coverage {judged:P0}\n" +
            $"Query/item text: {(withText > 0 ? $"available for {withText}" : "not available")} · images/thumbnails: {(ds.HasImages ? "available" : "none")} · " +
            $"relevance judgments: {(ds.HasRelevanceJudgments ? "yes" : "no")} · safety judgments: {(ds.HasSafetyJudgments ? "yes" : "no")}\n" +
            "Unjudged items are reported as unknown — never as safe, relevant or passing." +
            (ds.ValidationIssues.Count > 0 ? "\n" + string.Join("\n", ds.ValidationIssues.Take(5).Select(i => $"{i.Severity}: {i.Message}")) : "");
        OnPropertyChanged(nameof(HasDataset));
        settings.Update(s => SettingsStore.AddRecent(s.RecentDatasets, path));
        UpdateDatasetMatch();
        Status = "Dataset loaded. Check the run/dataset match below, then choose settings.";
    }

    private EvalKitDatasetIdentity? CapturedIdentity() =>
        new[] { A, B }.Select(s => s.IsCapturedSource ? s.Run?.RunDataset : null).OfType<DatasetReference>().Select(EvalKitDatasetIdentity.From).FirstOrDefault();

    private async Task FindCachedDatasetAsync()
    {
        var id = CapturedIdentity() ?? throw new InvalidOperationException("Select a captured run first; its exact dataset name, version and payload hash are used.");
        var path = await Task.Run(() => EvalKitFormat.FindCachedDataset(id, cli.DatasetCacheFor(DatasetStore.Container))
            ?? EvalKitFormat.FindCachedDataset(id, settings.Current.EvalKit.DatasetCacheDirectory), Token);
        if (path is null) { Status = $"Exact dataset {id.Name} v{id.Version} is not in the EvalKit cache. Use 'Fetch exact dataset (EvalKit CLI)'."; return; }
        await LoadDatasetAsync(path);
    }

    private async Task FetchDatasetAsync()
    {
        var id = CapturedIdentity() ?? throw new InvalidOperationException("Select a captured run first; its exact dataset name, version and payload hash are fetched.");
        var container = DatasetStore.Container;
        if (!dialogs.Confirm($"Run the EvalKit CLI to fetch exactly {id.Name} v{id.Version} (payload {id.PayloadHash}) from {DatasetStore.Label} with your own sign-in? Only this one dataset is downloaded.")) return;
        var path = await cli.FetchDatasetAsync(id, container, new Progress<string>(s => Status = s), Token);
        var state = container?.State ?? "Fetched from EvalKit's default store (published/working state not reported by the CLI)";
        await LoadDatasetAsync(path, container?.Classification, datasetState: state);
    }

    public sealed record DatasetStoreOption(string Label, EvalKitDatasetContainer? Container)
    {
        public override string ToString() => Label;
    }
    public static IReadOnlyList<DatasetStoreOption> DatasetStores { get; } =
        [new("EvalKit default store", null), .. EvalKitDatasetContainer.Known.Select(c => new DatasetStoreOption(c.ToString(), c))];
    public DatasetStoreOption DatasetStore { get; set; } = DatasetStores[0];

    private void UpdateDatasetMatch()
    {
        if (dataset is null)
        {
            DatasetMatch = HasCapturedDataset ? "Restore the exact dataset the selected runs were scored against."
                : "Select a prepared dataset above and click Load selected dataset. Browse to download or prepare another dataset.";
            return;
        }
        var lines = new List<string>();
        foreach (var s in new[] { A, B })
        {
            if (s.IsPsapiLive)
            {
                var errors = PsapiJob.CheckDataset(s.PsapiSpec!, dataset);
                lines.Add(errors.Count == 0 ? $"✔ {s.Title}: {s.PsapiSpec!.DisplayName} will run locally through the EvalKit PSAPI backend." +
                    (dataset.SourcePath?.EndsWith(".smds.json", StringComparison.OrdinalIgnoreCase) == true
                        ? " A local execution snapshot will be prepared; metrics retain the original dataset and judgments." : " This is an EvalKit dataset.")
                    : $"✖ {s.Title}: {string.Join(" ", errors)}");
                continue;
            }
            if (s.IsProviderSource) { lines.Add($"✔ {s.Title}: a new evaluation will be scored against this exact dataset."); continue; }
            if (s.Run is null) { lines.Add($"• {s.Title}: no run selected."); continue; }
            var mismatch = PairComparer.DatasetMismatches(s.Run.RunDataset, dataset.ToReference());
            lines.Add(mismatch.Count == 0 ? $"✔ {s.Title}: run matches dataset name, version and payload hash."
                : $"✖ {s.Title}: NOT comparable — {string.Join("; ", mismatch)}. Load matching runs and their exact dataset, or select a model provider for a new evaluation on this dataset.");
        }
        DatasetMatch = string.Join("\n", lines);
    }

    private async Task LoadDemoAsync()
    {
        await LoadDatasetAsync(Path.Combine(AppContext.BaseDirectory, "samples", "retrieval-it-helpdesk", "it-helpdesk.smds.json"));
        A.SelectProviderModel(DeterministicProvider.ProviderId, DeterministicProvider.Describe("hash-word-256"));
        B.SelectProviderModel(DeterministicProvider.ProviderId, DeterministicProvider.Describe("hash-char3-256"));
        Status = "Synthetic demo loaded: two deterministic baselines on a synthetic dataset. Results are not real model performance.";
    }

    // ---- run / load ------------------------------------------------------------------------------------------
    private string EvaluationKey(ModelConfiguration c, EvalDataset ds) =>
        $"{c.Fingerprint}|{ds.Id}|{ds.Version}|{ds.ContentHash}|K{k}|S{sampleLimit}";

    private async Task RunAsync()
    {
        InvalidateComparison();
        var pair = PairSelection.Validate(A.Configuration, B.Configuration);
        if (!pair.IsValid) throw new InvalidOperationException(pair.ToString());
        var full = dataset ?? throw new InvalidOperationException("Load a dataset first (step 3).");
        var (ds, ids) = PairComparer.Sample(full, sampleLimit);
        var depth = Math.Max(100, k);
        var captured = new Dictionary<PairSideViewModel, PairSide>();
        foreach (var s in new[] { A, B }.Where(s => s.IsCapturedSource))
        {
            var choice = s.Run ?? throw new InvalidOperationException($"{s.Title}: select a captured run.");
            var issues = PairComparer.DatasetMismatches(choice.RunDataset, full.ToReference());
            if (issues.Count > 0) throw new InvalidOperationException($"{s.Title}: comparison blocked before evaluation. {string.Join(" ", issues)} " +
                "Choose matching captured runs and their exact dataset, or use installed PSAPI models for a new evaluation on the Step 3 dataset.");
        }
        foreach (var s in new[] { A, B }.Where(s => s.IsCapturedSource))
        {
            var c = s.Run!;
            var r = await Task.Run(() => s.LoadCaptured(depth, Token, full.Queries.Select(q => q.Id).ToList()), Token)
                ?? throw new InvalidOperationException($"{s.Title}: the captured run has no results.");
            var inScope = (PairComparer.IsRetrievalTask(ds.Task) ? ds.Queries.Select(q => q.Id) : ds.Examples.Select(e => e.Id)).ToHashSet(StringComparer.Ordinal);
            if (!inScope.Overlaps(PairComparer.IsRetrievalTask(ds.Task) ? r.Queries.Select(q => q.QueryId) : r.Examples.Select(e => e.ExampleId)))
                throw new InvalidOperationException($"{s.Title}: the captured run has no query/example IDs in the selected dataset and scope. No model was invoked.");
            captured[s] = new PairSide { Configuration = c.Configuration, Result = r, RunDataset = c.RunDataset, Origin = c.Origin,
                SourceDescription = c.SourceDescription, RunId = c.RunId, RunAt = c.RunAt, CachedDepth = c.CachedDepth,
                Threshold = s.EffectiveThreshold, Classification = c.Classification };
        }
        if (captured.Count == 2)
        {
            var eligibility = PairComparer.CheckEligibility(ds, captured[A], captured[B], k);
            if (eligibility.Blocking.Count > 0) throw new InvalidOperationException(string.Join(" ", eligibility.Blocking));
        }

        var toRun = new List<PairSideViewModel>();
        foreach (var s in new[] { A, B })
            if (s.IsProviderSource && !(reuse && evaluated.ContainsKey(EvaluationKey(s.Configuration!, full)))) toRun.Add(s);
        var psapi = toRun.Where(s => s.IsPsapiLive).ToList();
        toRun = [.. toRun.Except(psapi)];
        foreach (var s in psapi)
        {
            var errors = PsapiJob.CheckDataset(s.PsapiSpec!, full);
            if (errors.Count > 0) throw new InvalidOperationException($"{s.Title} ({s.PsapiSpec!.DisplayName}): " + string.Join(" ", errors));
            var status = PsapiBackendStatus.Probe(cli);
            if (!status.CanRun) throw new InvalidOperationException($"{s.Title} ({s.PsapiSpec!.DisplayName}): {status.Summary}");
        }
        if (psapi.Count > 0 && !dialogs.Confirm($"Run {string.Join(" and ", psapi.Select(s => s.PsapiSpec!.DisplayName))} locally on CPU through the EvalKit PSAPI backend on '{full.Name}' v{full.Version} " +
            $"(all {full.Queries.Count} queries, {full.Documents.Count} documents; the sample limit is applied afterwards)? No network call is made and dataset content stays on this machine. This can take minutes for large corpora.")) return;
        var remote = toRun.Where(s => s.Model!.Properties.GetValueOrDefault("remote_inference") == "true" || providers.Find(s.Model.Identity.ProviderId)?.UsesNetwork == true && s.Model.Execution == ExecutionKind.ExternalProcess).ToList();
        if (remote.Count > 0 && !dialogs.Confirm($"{string.Join(" and ", remote.Select(s => s.Configuration!.Label))} will receive query and document text from '{full.Name}' " +
            $"(classification: {full.DataClassification}). Continue?")) return;

        if (toRun.Count > 0)
        {
            var plans = toRun.Select(s => new ModelPlan(s.Model!)).ToList();
            var check = EvaluationEngine.CheckCompatibility(plans, ds, ds.Task);
            if (!check.CanRun || check.Items.Any(i => i.Severity == ValidationSeverity.Error))
                throw new InvalidOperationException("Cannot evaluate both models: " + string.Join("; ", check.Items.Where(i => i.Severity == ValidationSeverity.Error).Select(i => $"{i.Model}: {i.Message}")));
            var config = new EvaluationConfig { K = k, RetainDepth = depth, BatchSize = 16, MaxConcurrency = 2, TimeoutSeconds = 1800,
                MetricIds = [], MetricVersions = [] };
            var progress = new Progress<EvaluationProgress>(p => Status = $"Invoking {p.ModelName} ({p.ModelIndex}/{p.ModelCount}): {p.Stage} {p.Completed}/{p.Total} · {p.FailedItems} failed · {p.Elapsed:mm\\:ss}");
            var run = await Task.Run(() => engine.RunAsync(ds, plans, config, progress, Token, "Pair: " + string.Join(" vs ", toRun.Select(s => s.Configuration!.DisplayName))), Token);
            for (int i = 0; i < toRun.Count; i++)
                evaluated[EvaluationKey(toRun[i].Configuration!, full)] = (run.Models[i], run.Dataset, depth, run.StartedAt, run.RunId);
        }
        PsapiDatasetBridge? bridge = null;
        if (psapi.Count > 0 && full.SourcePath!.EndsWith(".smds.json", StringComparison.OrdinalIgnoreCase))
            bridge = await PsapiDatasetBridge.CreateAsync(full, Path.Combine(AppPaths.Root, "psapi-datasets", Guid.NewGuid().ToString("N")), Token);
        foreach (var s in psapi)
            evaluated[EvaluationKey(s.Configuration!, full)] = await RunPsapiAsync(s, full, depth, bridge);

        PairSide Side(PairSideViewModel s)
        {
            if (s.IsProviderSource)
            {
                var e = evaluated[EvaluationKey(s.Configuration!, full)];
                return new PairSide { Configuration = s.Configuration!, Result = e.Result, RunDataset = e.Dataset, Origin = ResultOrigin.NewEvaluation,
                    SourceDescription = psapiNotes.GetValueOrDefault(EvaluationKey(s.Configuration!, full)) ?? $"New evaluation by this app (run {e.RunId})", RunId = e.RunId, RunAt = e.At, CachedDepth = e.Depth, Threshold = s.EffectiveThreshold };
            }
            return captured[s];
        }
        var (a, b) = await Task.Run(() => (Side(A), Side(B)), Token);
        A.SetScores(a.Result); B.SetScores(b.Result);
        var pairSettings = BuildSettings(sampleLimit > 0 ? ids : null);
        basis = (a, b, ds, pairSettings);
        await CompareAsync(Token);
        var invoked = toRun.Concat(psapi).ToList();
        Status = (invoked.Count > 0 ? $"Invoked {string.Join(", ", invoked.Select(s => s.Title))}; " : "No model invoked; ") +
            (result?.IsComparable == true ? $"loaded/compared results. {result.Recommendation.Headline}" : $"comparison blocked. {result?.Recommendation.Detail}");
    }

    /// <summary>
    /// Live PSAPI evaluation: writes an EvalKit job for the external PSAPI backend, runs it locally (CPU, no network) and loads its output.
    /// The job, log and outputs are kept under the app data folder for reproducibility.
    /// </summary>
    private async Task<(ModelRunResult Result, DatasetReference Dataset, int Depth, DateTimeOffset At, string RunId)> RunPsapiAsync(PairSideViewModel s, EvalDataset full, int depth, PsapiDatasetBridge? bridge)
    {
        var spec = s.PsapiSpec!;
        var status = PsapiBackendStatus.Probe(cli);
        var at = DateTimeOffset.UtcNow;
        var name = PsapiJob.RunName(spec, full.Name, at);
        var folder = Path.Combine(AppPaths.Root, "psapi-runs", name);
        Directory.CreateDirectory(folder);
        var jobPath = Path.Combine(folder, name + ".job.json");
        var job = PsapiJob.Build(spec, bridge?.ManifestPath ?? full.SourcePath!, status.PackageDirectory!, depth, name,
            bridge is null ? PsapiJob.TextSource(spec, full) : "inlineText");
        await File.WriteAllTextAsync(jobPath, job.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Token);
        Status = $"{s.Title}: running {spec.DisplayName} through the EvalKit PSAPI backend (local CPU)...";
        var output = await cli.RunJobAsync(jobPath, Path.Combine(folder, "output"), new Progress<string>(line => Status = $"{s.Title} · {spec.DisplayName}: {line}"), Token);
        var metrics = EvalKitFormat.FindMetricsFiles(output);
        if (metrics.Count != 1) throw new InvalidDataException($"{spec.DisplayName}: expected one EvalKit metrics envelope in the run output, found {metrics.Count}.");
        var ek = EvalKitFormat.ReadRun(metrics[0], null, full.DataClassification);
        var runDataset = bridge?.MatchResult(ek.Dataset) ?? PairSideViewModel.RunDatasetOf(ek);
        if (ek.BackendModelId is { } reported && !string.Equals(reported, spec.ModelId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The backend reported model '{reported}' but '{spec.ModelId}' was requested; the run was discarded.");
        var loaded = await Task.Run(() => EvalKitFormat.ReadResults(ek, Math.Max(depth, 1000), Token, full.Queries.Select(q => q.Id).ToList()), Token);
        var result = new ModelRunResult
        {
            Model = new ModelIdentity(PsapiProvider.ProviderId, spec.ModelId, s.Configuration!.Revision ?? "package version unknown"),
            DisplayName = spec.DisplayName,
            Execution = ExecutionKind.ExternalProcess,
            Status = loaded.Status,
            Queries = loaded.Queries,
            ScoresIncludeRanks = loaded.ScoresIncludeRanks,
            Warnings = [.. loaded.Warnings],
            Performance = new PerformanceStats
            {
                MeanLatencyMs = loaded.Performance.MeanLatencyMs,
                Runtime = $"EvalKit {ek.EvalKitVersion ?? "?"} / PSAPI external backend" + (ek.BackendModelVersion is { } mv ? $" (model {mv})" : ""),
                Device = ek.BackendDevice ?? "cpu",
                Measured = false
            }
        };
        if (ek.FailedQueryCount > 0) { result.FailedItems = ek.FailedQueryCount; result.Warnings.Add($"EvalKit reported {ek.FailedQueryCount} failed queries (onQueryError=skip)."); }
        result.Warnings.Add("Latency is EvalKit's whole-run query duration divided by query count (no per-query latencies are recorded), so P50/P95/P99 are unknown.");
        if (bridge is not null)
            result.Warnings.Add($"Local PSAPI adapter payload {bridge.ExecutionIdentity.PayloadHash} verified. App metrics use the original dataset's judgments, not EvalKit's converted summary metrics.");
        psapiNotes[EvaluationKey(s.Configuration!, full)] = $"New evaluation: EvalKit job '{name}' on the local PSAPI backend ({ek.BatchMetadata})";
        log.Write(LogLevel.Info, "psapi", $"PSAPI run {name}: job and outputs saved under the app data folder psapi-runs\\{name}.");
        return (result, runDataset, depth, at, ek.RunId);
    }
    private readonly Dictionary<string, string> psapiNotes = [];

    private void ScheduleRecalculation()
    {
        if (basis is null) return;
        recalculation?.Cancel();
        var cts = recalculation = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, cts.Token);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (cts.IsCancellationRequested) return;
                    await CompareAsync(cts.Token);
                    Status = $"Recalculated from captured scores (A threshold {A.ThresholdText.OrDefault("none")}, B threshold {B.ThresholdText.OrDefault("none")}, K={k}). No model was re-run.";
                }).Task.Unwrap();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status = "Recalculation failed: " + Redactor.Redact(ex.Message); }
        });
    }

    private async Task CompareAsync(CancellationToken ct)
    {
        if (basis is not { } x) return;
        var a = With(x.A, A.EffectiveThreshold); var b = With(x.B, B.EffectiveThreshold);
        var settingsK = BuildSettings(x.Settings.SampledIds);
        var r = await Task.Run(() => comparer.Compare(x.Dataset, a, b, settingsK, includeText: true, ct), ct);
        ct.ThrowIfCancellationRequested();
        Present(r);
    }

    private static PairSide With(PairSide s, double? threshold) => new()
    {
        Configuration = s.Configuration, Result = s.Result, RunDataset = s.RunDataset, Origin = s.Origin, SourceDescription = s.SourceDescription,
        RunId = s.RunId, RunAt = s.RunAt, CachedDepth = s.CachedDepth, Threshold = threshold, Classification = s.Classification
    };

    private void ClearResults()
    {
        Headline = Summary = Blocking = Warnings = Strengths = Limitations = "";
        MetricRows = []; PerformanceRows = []; Counts = []; Heatmap = []; Rows = []; CurveMetrics = [];
        CurvesA = []; CurvesB = []; Radar = null; RadarNote = ""; CompatibilityRows = []; AgreementText = "";
        SelectedRow = null; FilteredRows.Clear(); OnPropertyChanged(nameof(RowCountText));
        foreach (var n in new[] { nameof(Headline), nameof(Summary), nameof(Blocking), nameof(Warnings), nameof(Strengths), nameof(Limitations),
            nameof(MetricRows), nameof(PerformanceRows), nameof(Counts), nameof(Heatmap), nameof(Rows), nameof(CurveMetrics), nameof(CompatibilityRows), nameof(AgreementText) }) OnPropertyChanged(n);
    }

    private void Present(PairComparisonResult r)
    {
        Result = r;
        var ca = r.A.Configuration; var cb = r.B.Configuration;
        Headline = (r.IsComparable ? "" : "NOT COMPARABLE — ") + r.Recommendation.Headline;
        Summary =
            $"Model A: {ca.Label}{(ca.Endpoint is { } ea ? " · " + Redactor.Redact(ea) : "")} · {r.A.Origin} · {r.A.SourceDescription}\n" +
            $"Model B: {cb.Label}{(cb.Endpoint is { } eb ? " · " + Redactor.Redact(eb) : "")} · {r.B.Origin} · {r.B.SourceDescription}\n" +
            $"Dataset: {r.Dataset.Name} v{r.Dataset.Version} · hash {r.Dataset.ContentHash} · {r.DatasetClassification}\n" +
            $"Run times: A {r.A.RunAt?.ToString("u") ?? "unknown"} · B {r.B.RunAt?.ToString("u") ?? "unknown"}\n" +
            $"Configuration: K={r.Settings.K} · A threshold {PairComparer.Fmt(r.A.Threshold, "0.0000")} · B threshold {PairComparer.Fmt(r.B.Threshold, "0.0000")}" +
            $" · items {(r.Settings.SampledIds is { } s ? $"sample of {s.Count}" : "all")}\n" +
            (r.IsComparable ? $"Outcomes: A wins {r.AWins} · B wins {r.BWins} · ties {r.Ties} · unknown {r.Unknown}\n" : "Outcomes: not evaluated (eligibility failed).\n") +
            $"Confidence: {r.Recommendation.Confidence} — {r.Recommendation.ConfidenceReason}" +
            (r.Recommendation.SignTestP is { } p ? $" (sign test p={p:0.###})" : "") + "\n" + r.Recommendation.Detail +
            (r.IsLikeForLike ? "" : "\n⚠ This comparison is not fully like-for-like; see warnings.");
        Blocking = r.Blocking.Count == 0 ? "" : "Comparison blocked before scoring:\n" + string.Join("\n", r.Blocking.Select(x => "✖ " + x));
        Warnings = r.Warnings.Count == 0 ? "" : string.Join("\n", r.Warnings.Select(x => "⚠ " + x));
        Strengths = $"Model A strengths:\n{Bullets(r.StrengthsA)}\nModel A weaknesses:\n{Bullets(r.WeaknessesA)}\n\nModel B strengths:\n{Bullets(r.StrengthsB)}\nModel B weaknesses:\n{Bullets(r.WeaknessesB)}";
        Limitations = Bullets(r.Limitations);
        CompatibilityRows = [.. r.Compatibility.Select(c => new PairCompatibilityView(c.Aspect, c.Status switch
        {
            CompatibilityStatus.Pass => "✔ Pass", CompatibilityStatus.Note => "ℹ Note", CompatibilityStatus.Warning => "⚠ Warning",
            CompatibilityStatus.Blocked => "✖ Blocked", _ => "? Unknown"
        }, c.Detail))];
        var ag = r.Agreement;
        AgreementText = !r.IsComparable ? "Not evaluated: resolve the eligibility failures." : ag is null ? "Not applicable (agreement is computed for retrieval comparisons only)." :
            $"Queries compared: {ag.Queries}\n" +
            $"Mean top-{r.Settings.K} overlap (Jaccard): {PairComparer.Fmt(ag.MeanTopKOverlap, "0.000")}\n" +
            $"Mean rank correlation (Spearman ρ): {PairComparer.Fmt(ag.MeanRankCorrelation, "0.000")} over {ag.RankCorrelationQueries} queries\n" +
            $"Relevant documents retained: A {ag.RelevantA} · B {ag.RelevantB} · both {ag.RelevantBoth}\n" +
            $"Queries where B missed a relevant document A found: {ag.RelevantMissedByB} · where A missed one B found: {ag.RelevantMissedByA}\n" +
            $"Raw scores comparable across the two models: {(r.RawScoresComparable ? "yes (same provider and model)" : "NO — different score scales; compare ranks and judgment-based metrics only")}\n" +
            PairAgreement.Method;
        relevantQueries = [.. basis?.Dataset.Judgments.Where(j => j.Grade > 0).Select(j => j.QueryId) ?? []];
        OnPropertyChanged(nameof(CompatibilityRows)); OnPropertyChanged(nameof(AgreementText));

        MetricRows = [.. r.Metrics.Select(m =>
        {
            var d = m.Definition; bool bars = d.IsBounded01 && m.A?.Value is not null && m.B?.Value is not null;
            var better = m.Better switch { "A" => "▲ A better", "B" => "▼ B better", "Tie" => "● tie", _ => m.Definition.Direction == Direction.Neutral ? "– no preferred direction" : "? unknown (missing value)" };
            var note = string.Join(" ", new[] { m.A?.Note is { } na ? "A: " + na : null, m.B?.Note is { } nb ? "B: " + nb : null }.OfType<string>());
            return new PairMetricView(d.Name, d.Category.ToString(), m.DirectionNote, PairComparer.Fmt(m.A?.Value, d.Format), PairComparer.Fmt(m.B?.Value, d.Format),
                PairComparer.Signed(m.Delta, d.Format), better, bars ? BarWidth * Math.Clamp(m.A!.Value!.Value, 0, 1) : 0,
                bars ? BarWidth * Math.Clamp(m.B!.Value!.Value, 0, 1) : 0, bars, note);
        })];
        PerformanceRows = [.. r.Performance.Select(p => new PairPerformanceView(p.Name, PairComparer.Fmt(p.A, p.Format), PairComparer.Fmt(p.B, p.Format), PairComparer.Signed(p.Delta, p.Format), p.Basis))];
        Counts = !r.IsComparable ? [] :
        [
            new("Total items", r.CountsA.Total.ToString(), r.CountsB.Total.ToString()),
            new("Judged", r.CountsA.Judged.ToString(), r.CountsB.Judged.ToString()),
            new("Unjudged (unknown, never treated as safe/relevant)", r.CountsA.Unjudged.ToString(), r.CountsB.Unjudged.ToString()),
            new("Evaluated", r.CountsA.Evaluated.ToString(), r.CountsB.Evaluated.ToString()),
            new("Retained results (after threshold)", r.CountsA.Retained.ToString(), r.CountsB.Retained.ToString()),
            new("Filtered by threshold", r.CountsA.Filtered.ToString(), r.CountsB.Filtered.ToString()),
            new("Empty (no retained result)", r.CountsA.Empty.ToString(), r.CountsB.Empty.ToString()),
            new("Errors (model failed on the item)", r.CountsA.Errors.ToString(), r.CountsB.Errors.ToString()),
            new("Missing from run", r.CountsA.Missing.ToString(), r.CountsB.Missing.ToString())
        ];
        Heatmap = [.. r.Metrics.Select(m =>
        {
            var d = m.Delta; var scale = m.Definition.IsBounded01 ? 0.1 : Math.Max(1e-9, Math.Abs(m.A?.Value ?? 0) + Math.Abs(m.B?.Value ?? 0)) / 2;
            var alpha = d is { } v ? (int)(40 + 180 * Math.Clamp(Math.Abs(v) / scale, 0, 1)) : 0;
            var (bg, mark) = m.Better switch
            {
                "A" => ($"#{alpha:X2}{ColorA[1..]}", "▲ A"), "B" => ($"#{alpha:X2}{ColorB[1..]}", "▼ B"),
                "Tie" => ("#30808080", "● tie"), _ => ("#00000000", m.Definition.Direction == Direction.Neutral ? "– n/a" : "? unknown")
            };
            return new PairHeatCell(m.Definition.Name, $"{mark}  {PairComparer.Signed(d, m.Definition.Format)}", bg,
                $"{m.Definition.Name}: A {PairComparer.Fmt(m.A?.Value, m.Definition.Format)}, B {PairComparer.Fmt(m.B?.Value, m.Definition.Format)}, A−B {PairComparer.Signed(d, m.Definition.Format)}. {m.DirectionNote}");
        })];
        var radarRows = r.Metrics.Where(m => m.Definition.IsBounded01 && m.Definition.Category == MetricCategory.Quality && m.Definition.Direction == Direction.HigherIsBetter
            && m.A?.Value is not null && m.B?.Value is not null).ToList();
        if (radarRows.Count >= 3)
        {
            Radar = new RadarProjection([.. radarRows.Select(m => new RadarAxis(m.Definition.Id, m.Definition.Name, "Raw value"))],
                [new("A", "A: " + ca.DisplayName, ColorA, [.. radarRows.Select(m => m.A!.Value)]), new("B", "B: " + cb.DisplayName, ColorB, [.. radarRows.Select(m => m.B!.Value)])], []);
            RadarNote = "Radar: quality metrics on the same fixed 0–1 scale where higher is better. Harm metrics are excluded rather than inverted. Exact values are in the table.";
        }
        else { Radar = null; RadarNote = "Radar hidden: fewer than three quality metrics share a meaningful 0–1 higher-is-better scale."; }
        CurveMetrics = [.. r.CurveMetricIds];
        if (curveMetric is null || !CurveMetrics.Contains(curveMetric)) curveMetric = CurveMetrics.FirstOrDefault();
        BuildCurves();
        Rows = [.. r.Rows];
        foreach (var n in new[] { nameof(Headline), nameof(Summary), nameof(Blocking), nameof(Warnings), nameof(Strengths), nameof(Limitations),
            nameof(MetricRows), nameof(PerformanceRows), nameof(Counts), nameof(Heatmap), nameof(CurveMetrics), nameof(CurveMetric) }) OnPropertyChanged(n);
        ApplyRowFilter();
    }

    private static string Bullets(IEnumerable<string> items) { var list = items.ToList(); return list.Count == 0 ? "  • none identified" : string.Join("\n", list.Select(i => "  • " + i)); }

    private void BuildCurves()
    {
        List<ChartSeries> Curve(ThresholdCurve? c, string color, string who)
        {
            if (c is null || curveMetric is null || !c.Series.TryGetValue(curveMetric, out var ys)) return [];
            var name = result?.Metrics.FirstOrDefault(m => m.Definition.Id == curveMetric)?.Definition.Name ?? curveMetric;
            return [new ChartSeries($"{who} {name}", color, c.Thresholds, ys)];
        }
        CurvesA = Curve(result?.CurveA, ColorA, "A"); CurvesB = Curve(result?.CurveB, ColorB, "B");
    }

    public ObservableCollection<PairQueryRow> FilteredRows { get; } = [];
    private void ApplyRowFilter()
    {
        FilteredRows.Clear();
        foreach (var r in Rows.Where(r => rowFilter switch
        {
            "Model A wins" => r.Outcome == QueryOutcome.AWins,
            "Model B wins" => r.Outcome == QueryOutcome.BWins,
            "Ties" => r.Outcome == QueryOutcome.Tie,
            "Disagreements" => r.Flags.HasFlag(QueryFlags.Disagreement),
            "Relevant items missed" => relevantQueries.Contains(r.Id) && (r.A.RelevantRetained == 0 && !r.A.Missing || r.B.RelevantRetained == 0 && !r.B.Missing),
            "Missing content" => string.IsNullOrEmpty(r.Text) && r.ImagePath is null || r.A.Missing || r.B.Missing,
            "Safety concerns" => r.Flags.HasFlag(QueryFlags.SafetyConcern),
            "Empty results" => r.Flags.HasFlag(QueryFlags.Empty),
            "Errors" => r.Flags.HasFlag(QueryFlags.Error),
            "Unjudged" => r.Flags.HasFlag(QueryFlags.Unjudged),
            "Unknown outcome" => r.Outcome == QueryOutcome.Unknown,
            _ => true
        }).Take(5000)) FilteredRows.Add(r);
        OnPropertyChanged(nameof(RowCountText));
    }
    public string RowCountText => $"{FilteredRows.Count} of {Rows.Count} item(s) shown";

    // ---- report ----------------------------------------------------------------------------------------------
    public static string ApplicationVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private async Task DownloadAsync()
    {
        var r = result ?? throw new InvalidOperationException("Run or load a comparison first (step 5).");
        var confirmed = false;
        if (includeSampleContent && DetailedReportBuilder.IsSensitive(r))
        {
            confirmed = dialogs.Confirm("SENSITIVE OR UNCLASSIFIED DATA\n\nThe report will embed query text, labels, document ids and thumbnails. " +
                "Only export if you are authorized and the file will be stored and shared according to the data's handling rules.\n\nInclude sample-level content?");
            if (!confirmed) { Status = "Download cancelled; sample-level content was not exported."; return; }
        }
        if (dialogs.Save("Self-contained HTML report|*.html", ".html") is not { } path) return;
        var options = new DetailedReportOptions { IncludeSampleContent = includeSampleContent, SensitiveContentConfirmed = confirmed, ApplicationVersion = ApplicationVersion };
        var html = await Task.Run(() => DetailedReportBuilder.Build(r, options), Token);
        await Task.Run(() => AtomicFile.WriteAllText(path, html), Token);
        Status = "Detailed HTML saved: " + path + (includeSampleContent ? ""
            : DetailedReportBuilder.IsSensitive(r) ? " (aggregate only — no item IDs, per-item scores, text, labels or images)."
            : " (query text, labels and images excluded; item IDs and scores included).");
    }

    public void Dispose() { operation?.Cancel(); recalculation?.Cancel(); }
}

internal static class StringExtensions
{
    public static string OrDefault(this string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;
}
