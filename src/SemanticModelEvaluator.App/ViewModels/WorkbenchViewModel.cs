using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SemanticModelEvaluator.App.Services;
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

namespace SemanticModelEvaluator.App.ViewModels;

public sealed class ModelChoice(ModelDescriptor model) : ObservableObject
{
    public ModelDescriptor Model { get; } = model;
    private bool selected;
    public bool Selected { get => selected; set => SetProperty(ref selected, value); }
    public string Name => Model.DisplayName;
    public string Identity => Model.Identity.Key;
    public string Details => $"{Model.ProviderName} | {Model.Family} | {string.Join(", ", Model.Tasks)} | {Model.InputModality} -> {Model.OutputModality}\n" +
        $"{Model.Readiness}: {Model.ReadinessDetail}\nLicense: {Model.License} | {Model.EmbeddingDimension?.ToString() ?? "?"} dimensions | {Model.Quantization ?? "n/a"} | {Model.RuntimeRequirements}";
}

public sealed class MetricChoice(MetricDefinition definition) : ObservableObject
{
    public MetricDefinition Definition { get; } = definition;
    private bool selected = true;
    public bool Selected { get => selected; set => SetProperty(ref selected, value); }
}

public sealed record InspectorItem(string Id, string Input, string GroundTruth, string? ImagePath);
public sealed record HeatCell(string Scenario, string Metric, string Value, string Delta, string Color);
public sealed record ChartSeries(string Name, string Color, double[] X, double?[] Y);
public sealed record AnalysisResult(List<ScenarioEvaluation> Evaluations, ComparisonTable Table,
    DataView Grid, List<HeatCell> Heatmap, List<ChartSeries> Curves, RadarProjection Radar,
    List<ChartSeries> Histograms, string Errors, string Performance);

public sealed class WorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly ProviderRegistry providers;
    private readonly EvaluationEngine engine;
    private readonly MetricRegistry metrics;
    private readonly IUserDialogs dialogs;
    private readonly SettingsStore settings;
    private readonly AppLog log;
    private readonly NetworkPolicy network;
    private CancellationTokenSource? runCancellation;
    private CancellationTokenSource? analysisCancellation;
    private Workspace workspace = new();
    private string? workspacePath;
    private EvalDataset? dataset;
    private bool restoring;
    private List<ScenarioEvaluation> evaluations = [];
    private ComparisonTable? comparison;
    private string status = "Load a dataset and select models. All bundled examples are synthetic.";
    private bool busy;
    private bool dirty;
    private int page;
    private string search = "";
    private Scenario? selectedScenario;
    private Scenario? reference;
    private InspectorItem? selectedItem;
    private string filter = "";
    private string? curveMetric;
    private double threshold;
    private bool useThreshold;
    private int k = 5;
    private double min;
    private double max = 1;
    private DataView? metricGrid;
    private DataView? inspector;
    private List<ChartSeries> curves = [];
    private List<ChartSeries> histograms = [];
    private RadarProjection? radar;
    private List<HeatCell> heatmap = [];
    private string summary = "";
    private string errors = "";
    private string performance = "";
    private string ranking = "";
    private string notes = "";
    private string exportFormat = "JSON";
    private string scenarioName = "Threshold experiment";
    private RankingPolicy? rankingPolicy;
    private bool contextVisible = true;
    private int comparisonTab;
    private string rankingWeights = "";

    public WorkbenchViewModel(ProviderRegistry providers, EvaluationEngine engine, MetricRegistry metrics,
        IUserDialogs dialogs, SettingsStore settings, AppLog log, NetworkPolicy network)
    {
        this.providers = providers; this.engine = engine; this.metrics = metrics;
        this.dialogs = dialogs; this.settings = settings; this.log = log; this.network = network;
        RefreshCommand = Command(DiscoverAsync);
        SearchCommand = Command(SearchAsync);
        SampleCommand = Command(LoadSampleAsync);
        DatasetCommand = Command(async () => { if (dialogs.Open("Dataset manifest|*.smds.json;*.manifest.json") is { } path) await LoadDatasetAsync(path); });
        ImportCommand = Command(ImportPredictionsAsync);
        RegisterCommand = Command(RegisterOnnxAsync);
        ValidateCommand = Command(ValidateAsync);
        DownloadCommand = Command(DownloadAsync);
        RunCommand = Command(RunAsync);
        CancelCommand = new RelayCommand(() => runCancellation?.Cancel());
        OpenCommand = Command(OpenAsync);
        SaveCommand = Command(() => SaveAsync(false));
        SaveAsCommand = Command(() => SaveAsync(true));
        DuplicateCommand = new RelayCommand(() => { workspace = WorkspaceService.Duplicate(workspace, workspace.Name + " copy"); workspacePath = null; Dirty = true; NotifyTitle(); });
        ExportCommand = Command(ExportAsync);
        SaveScenarioCommand = new RelayCommand(SaveScenario);
        ResetThresholdCommand = new RelayCommand(() => UseThreshold = false);
        ThemeCommand = new RelayCommand(() => ThemeRequested?.Invoke());
        ToggleContextCommand = new RelayCommand(() => ContextVisible = !ContextVisible);
        MappingCommand = new RelayCommand(() => MappingRequested?.Invoke());
        ClearCacheCommand = Command(() =>
        {
            if (dialogs.Confirm("Clear this application's embedding cache? Saved results and model downloads are kept."))
            {
                new EmbeddingCache(AppPaths.EmbeddingCache).Clear();
                Status = "Embedding cache cleared.";
            }
            return Task.CompletedTask;
        });
        ApplyRankingCommand = new RelayCommand(() =>
        {
            try
            {
                var weights = new Dictionary<string, double>();
                foreach (var item in RankingWeights.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) ||
                        !double.IsFinite(weight) || weight < 0 || metrics.Find(parts[0]) is not { } metric || metric.Definition.Direction == Direction.Neutral)
                        throw new InvalidDataException("Each weight must be metric_id=nonnegative_number, using a directional metric.");
                    weights.Add(parts[0], weight);
                }
                if (!weights.Values.Any(w => w > 0)) throw new InvalidDataException("At least one weight must be positive.");
                var policy = new RankingPolicy { Name = "Custom priorities", Weights = weights };
                RankingPolicies.Add(policy); RankingPolicy = policy; MarkDirty();
            }
            catch (Exception ex) { Status = Redactor.Redact(ex.Message); }
        });
        OfflineCommand = new RelayCommand(() =>
        {
            network.OfflineMode = !network.OfflineMode;
            settings.Update(s => s.OfflineMode = network.OfflineMode);
            OnPropertyChanged(nameof(NetworkStatus));
        });
    }

    public event Action? ThemeRequested;
    public event Action? MappingRequested;
    public ObservableCollection<ModelChoice> Models { get; } = [];
    public ObservableCollection<MetricChoice> Metrics { get; } = [];
    public ObservableCollection<Scenario> Scenarios { get; } = [];
    public ObservableCollection<InspectorItem> Items { get; } = [];
    public ObservableCollection<RankingPolicy> RankingPolicies { get; } = [];
    public IEnumerable<string> ExportFormats => ["JSON", "CSV", "Text", "HTML"];
    public IEnumerable<string> CurveMetrics => Metrics.Where(m => m.Selected).Select(m => m.Definition.Id);
    public Workspace Workspace => workspace;
    public EvalDataset? Dataset => dataset;
    public bool Busy { get => busy; private set { SetProperty(ref busy, value); OnPropertyChanged(nameof(Idle)); } }
    public bool Idle => !Busy;
    public bool Dirty { get => dirty; private set { SetProperty(ref dirty, value); NotifyTitle(); } }
    public string Title => AppPaths.AppName + " | " + workspace.Name + (Dirty ? " *" : "");
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public int Page { get => page; set { if (SetProperty(ref page, value)) { workspace.View.ActiveView = value.ToString(); } TabIndex = value + 1; } }
    private int tabIndex;
    /// <summary>Navigation index: 0 is the two-model comparison tab; 1..6 map to workbench pages 0..5.</summary>
    public int TabIndex { get => tabIndex; set { if (SetProperty(ref tabIndex, value) && value >= 1) Page = value - 1; } }
    public string Search { get => search; set => SetProperty(ref search, value); }
    public string NetworkStatus => network.OfflineMode ? "Offline: local/cache only" : "Network enabled: explicit discovery/download only";
    public string DatasetSummary => dataset is null ? "No verified dataset loaded. Open a manifest or use Dataset Studio."
        : $"{dataset.Name} | v{dataset.Version} | {dataset.Task} | {dataset.RecordCount} inputs, {dataset.Documents.Count} documents\n" +
        $"Judgment coverage: {(dataset.Queries.Count > 0 ? dataset.JudgmentCoverage : dataset.LabelCoverage):P1} | {dataset.DataClassification} | {(dataset.IsSynthetic ? "SYNTHETIC - not real model performance" : "User dataset")}\n" +
        $"SHA-256: {dataset.ContentHash}\n{string.Join("\n", dataset.ValidationIssues.Select(i => $"{i.Severity}: {i.Message}"))}";
    public string ModelSummary => string.Join("\n", Models.Where(m => m.Selected).Select(m => m.Identity));
    public string Compatibility => dataset is null ? "Load a dataset first." : string.Join("\n",
        EvaluationEngine.CheckCompatibility(Plans(), dataset, dataset.Task).Items.Select(i => $"{i.Severity}: {i.Model}: {i.Message}"));
    public string Definitions => string.Join("\n\n", Metrics.Where(m => m.Selected).Select(m =>
        $"{m.Definition.Name} v{m.Definition.Version} ({m.Definition.DirectionText})\n{m.Definition.Explanation}\nMissing data: {m.Definition.MissingDataPolicy}"));
    public int BatchSize { get; set; } = 16;
    public int Concurrency { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 1800;
    public bool UseCache { get; set; } = true;
    public string OutputDirectory { get; set; } = AppPaths.Runs;
    public bool IncludeExamples { get; set; }
    public string Notes { get => notes; set { if (SetProperty(ref notes, value)) { workspace.Notes = value; MarkDirty(); } } }
    public string ExportFormat { get => exportFormat; set => SetProperty(ref exportFormat, value); }
    public string ScenarioName { get => scenarioName; set => SetProperty(ref scenarioName, value); }
    public string ScenarioNotes
    {
        get => SelectedScenario?.Notes ?? "";
        set { if (SelectedScenario is not null) { SelectedScenario.Notes = value; MarkDirty(); OnPropertyChanged(); } }
    }
    public int ComparisonTab { get => comparisonTab; set { if (SetProperty(ref comparisonTab, value)) { workspace.View.ComparatorTab = value.ToString(); MarkDirty(); } } }
    public string RankingWeights { get => rankingWeights; set => SetProperty(ref rankingWeights, value); }
    public bool ContextVisible { get => contextVisible; set => SetProperty(ref contextVisible, value); }
    public string Filter { get => filter; set { if (SetProperty(ref filter, value)) { workspace.View.QueryFilter = value; RebuildItems(); MarkDirty(); } } }
    public Scenario? SelectedScenario
    {
        get => selectedScenario;
        set
        {
            if (!SetProperty(ref selectedScenario, value) || value is null) return;
            restoring = true;
            var result = FindResult(value);
            (min, max) = ThresholdEngine.ScoreRange(result is null ? [] : [result]);
            threshold = value.Threshold ?? min; useThreshold = value.Threshold.HasValue; k = value.K;
            restoring = false;
            OnPropertyChanged(nameof(Minimum)); OnPropertyChanged(nameof(Maximum));
            OnPropertyChanged(nameof(Threshold)); OnPropertyChanged(nameof(UseThreshold)); OnPropertyChanged(nameof(K));
            workspace.View.FocusModelKey = value.ModelKey;
            workspace.View.FocusScenarioId = value.Id;
            OnPropertyChanged(nameof(ScenarioNotes));
            UpdateInspector();
        }
    }
    public Scenario? Reference
    {
        get => reference;
        set { if (SetProperty(ref reference, value)) { workspace.ReferenceScenarioId = value?.Id; ScheduleAnalysis(); } }
    }
    public InspectorItem? SelectedItem { get => selectedItem; set { if (SetProperty(ref selectedItem, value)) UpdateInspector(); } }
    public double Minimum => min;
    public double Maximum => max;
    public double Threshold
    {
        get => threshold;
        set { if (double.IsFinite(value) && SetProperty(ref threshold, value)) UpdateThreshold(); }
    }
    public bool UseThreshold { get => useThreshold; set { if (SetProperty(ref useThreshold, value)) UpdateThreshold(); } }
    public int K { get => k; set { if (value is >= 1 and <= 1000 && SetProperty(ref k, value)) UpdateThreshold(); } }
    public string? CurveMetric { get => curveMetric; set { if (SetProperty(ref curveMetric, value)) { workspace.View.CurveMetricId = value; ScheduleAnalysis(); } } }
    public DataView? MetricGrid { get => metricGrid; private set => SetProperty(ref metricGrid, value); }
    public DataView? Inspector { get => inspector; private set => SetProperty(ref inspector, value); }
    public List<ChartSeries> Curves { get => curves; private set => SetProperty(ref curves, value); }
    public List<ChartSeries> Histograms { get => histograms; private set => SetProperty(ref histograms, value); }
    public RadarProjection? Radar { get => radar; private set => SetProperty(ref radar, value); }
    public List<HeatCell> Heatmap { get => heatmap; private set => SetProperty(ref heatmap, value); }
    public string Summary { get => summary; private set => SetProperty(ref summary, value); }
    public string Errors { get => errors; private set => SetProperty(ref errors, value); }
    public string Performance { get => performance; private set => SetProperty(ref performance, value); }
    public string Ranking { get => ranking; private set => SetProperty(ref ranking, value); }
    public RankingPolicy? RankingPolicy
    {
        get => rankingPolicy;
        set
        {
            if (!SetProperty(ref rankingPolicy, value)) return;
            workspace.View.RankingPolicyName = value?.Name;
            workspace.View.RankingWeights = value is null ? [] : new(value.Weights);
            RankingWeights = value is null ? "" : string.Join(", ", value.Weights.Select(w => $"{w.Key}={w.Value.ToString(CultureInfo.InvariantCulture)}"));
            UpdateRanking();
        }
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand SampleCommand { get; }
    public IAsyncRelayCommand DatasetCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IAsyncRelayCommand RegisterCommand { get; }
    public IAsyncRelayCommand ValidateCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SaveAsCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand SaveScenarioCommand { get; }
    public IRelayCommand ResetThresholdCommand { get; }
    public IRelayCommand DuplicateCommand { get; }
    public IRelayCommand ThemeCommand { get; }
    public IRelayCommand OfflineCommand { get; }
    public IRelayCommand ToggleContextCommand { get; }
    public IRelayCommand MappingCommand { get; }
    public IRelayCommand ApplyRankingCommand { get; }
    public IAsyncRelayCommand ClearCacheCommand { get; }

    private AsyncRelayCommand Command(Func<Task> action) => new(async () =>
    {
        if (Busy) return;
        Busy = true;
        using var cts = new CancellationTokenSource();
        runCancellation = cts;
        try { await action(); }
        catch (OperationCanceledException) { Status = "Operation cancelled. Completed runs are preserved."; }
        catch (Exception ex) { Status = "Error: " + Redactor.Redact(ex.Message); log.Error("workbench", ex.GetType().Name + ": operation failed; details shown in UI."); }
        finally { runCancellation = null; Busy = false; }
    });

    private CancellationToken Token => runCancellation?.Token ?? CancellationToken.None;
    private void NotifyTitle() => OnPropertyChanged(nameof(Title));
    private void MarkDirty() { if (!restoring) Dirty = true; }
    private List<ModelPlan> Plans() => [.. Models.Where(m => m.Selected).Select(m => new ModelPlan(m.Model))];
    private void AddModel(ModelDescriptor model, bool select = false)
    {
        var old = Models.FirstOrDefault(m => m.Identity == model.Identity.Key);
        if (old is not null) { select |= old.Selected; Models.Remove(old); }
        var choice = new ModelChoice(model) { Selected = select };
        choice.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(ModelSummary)); OnPropertyChanged(nameof(Compatibility)); };
        Models.Add(choice);
    }
    public async Task InitializeAsync() => await RefreshCommand.ExecuteAsync(null);

    private async Task DiscoverAsync()
    {
        var found = await Task.Run(() => providers.DiscoverAllAsync(new ModelQuery(), Token), Token);
        foreach (var m in found.Models) AddModel(m);
        Status = found.Errors.Count > 0 ? string.Join("\n", found.Errors) : $"{Models.Count} local/cached model cards. No network used.";
    }
    private async Task SearchAsync()
    {
        if (!dialogs.Confirm("Send the model search text to huggingface.co? Dataset text will not be sent.")) return;
        var found = await Task.Run(() => providers.Get(HuggingFaceProvider.ProviderId).DiscoverAsync(new ModelQuery(Search, AllowNetwork: true), Token), Token);
        foreach (var m in found) AddModel(m);
        Status = $"Found {found.Count} model cards. Read compatibility and license before downloading.";
    }
    private async Task RegisterOnnxAsync()
    {
        if (dialogs.Folder() is not { } folder) return;
        ((LocalOnnxProvider)providers.Get(LocalOnnxProvider.ProviderId)).Register(folder);
        await DiscoverAsync();
    }
    private async Task ImportPredictionsAsync()
    {
        if (dataset is null) throw new InvalidOperationException("Load a dataset first so the prediction task is explicit.");
        if (dialogs.Open("Predictions|*.csv;*.json;*.jsonl;*.tsv") is not { } file) return;
        var task = dataset.Task;
        var model = await Task.Run(() => ((ImportedPredictionsProvider)providers.Get("imported")).Register(file, Path.GetFileNameWithoutExtension(file), task), Token);
        AddModel(model, true); Status = "Registered predictions for " + task + ". Select Run Evaluation to validate IDs and scores.";
    }
    private async Task ValidateAsync()
    {
        var messages = new List<string>();
        foreach (var plan in Plans())
        {
            var report = await providers.Get(plan.Model.Identity.ProviderId).ValidateAsync(plan.Model, Token);
            messages.Add(plan.Model.DisplayName + ": " + report);
        }
        Status = messages.Count > 0 ? string.Join("\n", messages) : "Select models to validate.";
    }
    private async Task DownloadAsync()
    {
        var selected = Plans().Where(p => p.Model.Readiness is ReadinessState.NeedsDownload or ReadinessState.RequiresAuthentication).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select a downloadable Hugging Face model first.");
        if (!dialogs.Confirm("Download selected model assets from Hugging Face? Check each model's license. Dataset content is not uploaded.")) return;
        foreach (var p in selected)
        {
            if (providers.Get(p.Model.Identity.ProviderId) is not IAcquiringModelProvider acquiring)
                throw new NotSupportedException("This provider requires preparation in its own application.");
            var progress = new Progress<ProgressInfo>(p => Status = p.Message + (p.Fraction is { } f ? $" {f:P0}" : ""));
            var ready = await acquiring.AcquireAsync(p.Model, progress, Token);
            var validation = await acquiring.ValidateAsync(ready, Token);
            if (!validation.IsValid) throw new InvalidDataException(validation.ToString());
            AddModel(ready, true);
        }
        Status = "Downloads validated. Ready for local CPU evaluation.";
    }
    private bool CanReplaceWorkspace() => !Dirty || dialogs.Confirm("Replace the current workspace? Unsaved scenarios and notes will be lost. Save first to retain them.");
    public async Task LoadDatasetAsync(string path)
    {
        if (!CanReplaceWorkspace()) return;
        var loaded = await Task.Run(() => DatasetLoader.Load(path), Token);
        DatasetPolicy.Ensure(loaded, DatasetOperation.Preview);
        if (loaded.ValidationIssues.Any(i => i.Severity == ValidationSeverity.Error))
            throw new InvalidDataException(string.Join("\n", loaded.ValidationIssues.Select(i => i.Message)));
        analysisCancellation?.Cancel();
        workspace = new Workspace { Name = loaded.Name, Dataset = loaded.ToReference() };
        workspacePath = null; dataset = loaded; Notes = "";
        Scenarios.Clear(); selectedScenario = null; reference = null; evaluations = []; comparison = null;
        MetricGrid = null; Inspector = null; Curves = []; Histograms = []; Radar = null; Heatmap = [];
        Summary = ""; Errors = ""; Performance = ""; Ranking = "";
        SetupDataset();
        OnPropertyChanged(nameof(SelectedScenario)); OnPropertyChanged(nameof(Reference));
        Dirty = true; Status = "Dataset loaded. Confirm models and metrics in Run Evaluation.";
    }
    private async Task LoadSampleAsync()
    {
        await LoadDatasetAsync(Path.Combine(AppContext.BaseDirectory, "samples", "retrieval-it-helpdesk", "it-helpdesk.smds.json"));
        if (dataset?.Id != "sample-it-helpdesk") return;
        foreach (var model in Models) model.Selected = false;
        AddModel(DeterministicProvider.Describe("hash-word-256"), true);
        AddModel(DeterministicProvider.Describe("hash-char3-256"), true);
        Page = 2;
    }
    private void SetupDataset()
    {
        Metrics.Clear(); RankingPolicies.Clear();
        if (dataset is not null)
        {
            foreach (var definition in metrics.Compatible(dataset.Task, dataset))
            {
                var choice = new MetricChoice(definition)
                {
                    Selected = workspace.View.SelectedMetricIds.Count == 0
                        ? definition.Category != MetricCategory.Performance
                        : workspace.View.SelectedMetricIds.Contains(definition.Id)
                };
                choice.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(CurveMetrics)); OnPropertyChanged(nameof(Definitions)); ScheduleAnalysis(); };
                Metrics.Add(choice);
            }
            foreach (var policy in Core.Comparison.RankingPolicy.Presets(dataset.Task)) RankingPolicies.Add(policy);
        }
        rankingPolicy = RankingPolicies.FirstOrDefault();
        curveMetric = workspace.View.CurveMetricId ?? Metrics.FirstOrDefault(m => m.Selected)?.Definition.Id;
        filter = workspace.View.QueryFilter;
        RebuildItems();
        foreach (var name in new[] { nameof(Dataset), nameof(DatasetSummary), nameof(Compatibility), nameof(Definitions), nameof(CurveMetrics), nameof(CurveMetric), nameof(Filter), nameof(RankingPolicy) })
            OnPropertyChanged(name);
    }
    private async Task RunAsync()
    {
        var ds = dataset ?? throw new InvalidOperationException("Load a dataset first.");
        var plans = Plans();
        var check = EvaluationEngine.CheckCompatibility(plans, ds, ds.Task);
        if (!check.CanRun || check.Items.Any(i => i.Severity == ValidationSeverity.Error))
            throw new InvalidOperationException("Resolve compatibility errors before running.\n" + Compatibility);
        var ids = Metrics.Where(m => m.Selected).Select(m => m.Definition.Id).ToList();
        if (ids.Count == 0) throw new InvalidOperationException("Select at least one compatible metric.");
        var config = new EvaluationConfig { K = K, RetainDepth = Math.Max(100, K), BatchSize = BatchSize, MaxConcurrency = Concurrency,
            TimeoutSeconds = TimeoutSeconds, UseEmbeddingCache = UseCache, OutputDirectory = OutputDirectory, MetricIds = ids,
            MetricVersions = Metrics.ToDictionary(m => m.Definition.Id, m => m.Definition.Version) };
        var progress = new Progress<EvaluationProgress>(p =>
            Status = $"{p.ModelIndex}/{p.ModelCount} {p.ModelName}: {p.Stage} | {p.Completed}/{p.Total}, {p.Remaining} remaining | {p.Elapsed:mm\\:ss} | {p.FailedItems} failed\n{p.Warning}");
        var run = await Task.Run(() => engine.RunAsync(ds, plans, config, progress, Token), Token);
        workspace.Runs.Add(run);
        foreach (var plan in plans)
            if (!workspace.Models.Any(m => m.Identity == plan.Model.Identity)) workspace.Models.Add(plan.Model.Clone());
        workspace.View.SelectedMetricIds = ids;
        foreach (var result in run.Models)
        {
            var scenario = new Scenario { Name = result.DisplayName + $" (run {workspace.Runs.Count})", RunId = run.RunId,
                ModelKey = result.Model.Key, K = K, Color = Palette[Scenarios.Count % Palette.Length] };
            Scenarios.Add(scenario); workspace.Scenarios.Add(scenario);
        }
        SelectedScenario = Scenarios.LastOrDefault();
        Reference ??= Scenarios.FirstOrDefault();
        Dirty = true; Page = 3;
        Status = Redactor.Redact($"{run.Status}. {string.Join(" | ", run.Models.Select(m => $"{m.DisplayName}: {m.Status} {m.Error}"))}");
        await RecalculateAsync();
    }
    public static readonly string[] Palette = ["#0072B2", "#D55E00", "#009E73", "#CC79A7", "#6F4E7C", "#8C6D00"];
    private ModelRunResult? FindResult(Scenario s) => workspace.Runs.FirstOrDefault(r => r.RunId == s.RunId)?.Find(s.ModelKey);
    private void UpdateThreshold()
    {
        if (restoring || SelectedScenario is null) return;
        SelectedScenario.Threshold = UseThreshold ? Threshold : null; SelectedScenario.K = K;
        workspace.View.Thresholds[SelectedScenario.ModelKey] = SelectedScenario.Threshold;
        workspace.View.K = K; MarkDirty(); ScheduleAnalysis();
    }
    private void SaveScenario()
    {
        if (SelectedScenario is null) { Status = "Run an evaluation before saving a scenario."; return; }
        var copy = SelectedScenario.Copy(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = ScenarioName;
        copy.Color = Palette[Scenarios.Count % Palette.Length];
        Scenarios.Add(copy); workspace.Scenarios.Add(copy); SelectedScenario = copy; MarkDirty(); ScheduleAnalysis();
    }
    private void ScheduleAnalysis()
    {
        if (!restoring) _ = RecalculateAsync();
    }
    public async Task RecalculateAsync()
    {
        analysisCancellation?.Cancel();
        using var cts = new CancellationTokenSource();
        analysisCancellation = cts;
        try
        {
            var ds = dataset;
            if (ds is null || Scenarios.Count == 0) return;
            var defs = Metrics.Where(m => m.Selected).Select(m => m.Definition).ToArray();
            foreach (var run in workspace.Runs)
                foreach (var definition in defs)
                    if (run.Config.MetricVersions.TryGetValue(definition.Id, out var version) && version != definition.Version)
                        throw new InvalidDataException($"Run {run.RunId} requires {definition.Id} v{version}; installed v{definition.Version}. Re-evaluate explicitly rather than substitute a metric implementation.");
            var inputs = Scenarios.Select(s => (Scenario: s.Copy(), Result: FindResult(s))).Where(x => x.Result is not null).ToArray();
            if (inputs.Length == 0) return;
            var refId = Reference?.Id ?? inputs[0].Scenario.Id;
            var selectedMetric = CurveMetric ?? defs.FirstOrDefault()?.Id;
            await Task.Delay(100, cts.Token);
            var result = await Task.Run(() => Analyze(ds, inputs.Select(x => (x.Scenario, x.Result!)).ToArray(), defs, refId, selectedMetric, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            evaluations = result.Evaluations; comparison = result.Table; MetricGrid = result.Grid;
            Heatmap = result.Heatmap; Curves = result.Curves; Histograms = result.Histograms;
            Radar = result.Radar; Errors = result.Errors; Performance = result.Performance;
            Summary = string.Join(" | ", evaluations.Select(e => $"{e.Scenario.Name}: {e.Snapshot.RetainedResults} retained, {e.Snapshot.EmptyQueries} empty, {e.Snapshot.FailedItems} missing/failed"));
            workspace.View.SelectedMetricIds = [.. defs.Select(d => d.Id)];
            UpdateInspector(); UpdateRanking();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            comparison = null; evaluations = []; MetricGrid = null; Curves = []; Histograms = []; Radar = null; Heatmap = [];
            Summary = Status = "Analysis blocked: " + Redactor.Redact(ex.Message);
            log.Error("analysis", ex.GetType().Name + ": analysis blocked; details shown in UI.");
        }
        finally { if (ReferenceEquals(analysisCancellation, cts)) analysisCancellation = null; }
    }
    private AnalysisResult Analyze(EvalDataset ds, (Scenario Scenario, ModelRunResult Result)[] inputs,
        MetricDefinition[] defs, string referenceId, string? curveMetric, CancellationToken ct)
    {
        var te = new ThresholdEngine(metrics);
        var evals = inputs.Select(x => new ScenarioEvaluation(x.Scenario, x.Result,
            te.Evaluate(ds, x.Result, x.Scenario.Threshold, x.Scenario.K, defs.Select(d => d.Id)))).ToList();
        var table = ComparisonEngine.Compare(evals, referenceId, defs);
        var grid = new DataTable(); grid.Columns.Add("Metric"); grid.Columns.Add("Direction");
        for (int i = 0; i < inputs.Length; i++) grid.Columns.Add($"{i + 1}: {inputs[i].Scenario.Name}");
        var heat = new List<HeatCell>();
        foreach (var row in table.Rows)
        {
            var values = new List<object> { row.Metric.Name, row.Metric.DirectionText };
            foreach (var cell in row.Cells)
            {
                var value = cell.Value?.ToString(row.Metric.Format, CultureInfo.InvariantCulture) ?? "n/a";
                var delta = cell.Delta?.ToString("+0.000;-0.000;0", CultureInfo.InvariantCulture) ?? "n/a";
                values.Add($"{value} | delta {delta}\nn={cell.SampleCount}, judged={cell.JudgedCount}, missing={cell.MissingCount}\n{cell.Note}");
                heat.Add(new HeatCell(inputs.First(x => x.Scenario.Id == cell.ScenarioId).Scenario.Name, row.Metric.Name, value,
                    $"{delta} ({cell.Assessment})", cell.Assessment == DeltaAssessment.Better ? "#0072B2" : cell.Assessment == DeltaAssessment.Worse ? "#B34D00" : "#596579"));
            }
            grid.Rows.Add(values.ToArray());
        }
        var curves = new List<ChartSeries>(); var histograms = new List<ChartSeries>();
        foreach (var e in evals)
        {
            ct.ThrowIfCancellationRequested();
            var (min, max) = ThresholdEngine.ScoreRange([e.Result]);
            if (curveMetric is not null)
            {
                var sweep = te.Sweep(ds, e.Result, ThresholdEngine.CandidateThresholds(min, max, 31), e.Scenario.K, [curveMetric], ct);
                curves.Add(new(e.Scenario.Name, e.Scenario.Color, sweep.Thresholds, sweep.Series[curveMetric]));
            }
            var bins = ScoreHistogram.Build(e.Result.AllScores(), min, max, 20);
            histograms.Add(new(e.Scenario.Name, e.Scenario.Color, [.. bins.Select(b => (b.Start + b.End) / 2)], [.. bins.Select(b => (double?)b.Count)]));
        }
        var failures = ErrorAnalysis.Analyze(ds, evals);
        var errors = string.Join("\n", failures.All.Select(f => $"{f.ItemId}: {f.Kind} | {string.Join(", ", f.ScenarioIds.Select(id => evals.First(e => e.Scenario.Id == id).Scenario.Name))}"));
        errors += "\n" + Redactor.Redact(string.Join("\n", evals.Select(e => $"{e.Scenario.Name}: {e.Result.Status} {e.Result.Error}\n{string.Join("\n", e.Result.Warnings)}")));
        var performance = "Measurements are not comparable across different hardware, runtimes, concurrency or cache settings. Imported timings are unverified.\n\n" +
            string.Join("\n\n", evals.Select(e => $"{e.Scenario.Name}: {e.Result.Performance.Runtime} | {e.Result.Performance.Device}\n" +
                $"Measured locally: {e.Result.Performance.Measured} | Mean {e.Result.Performance.MeanLatencyMs:0.00} ms | P95 {e.Result.Performance.P95LatencyMs:0.00} ms\n" +
                $"Load {e.Result.Performance.LoadMs:0.0} ms | Throughput {e.Result.Performance.ThroughputPerSecond:0.0}/s | Peak app process memory {e.Result.Performance.PeakWorkingSetBytes / 1048576.0:0.0} MB"));
        performance += "\n\n" + string.Join("\n", workspace.Runs.Where(r => inputs.Any(i => i.Scenario.RunId == r.RunId))
            .Select(r => $"Run {r.RunId}: {r.Environment}\nBatch={r.Config.BatchSize}; concurrency={r.Config.MaxConcurrency}; cache={r.Config.UseEmbeddingCache}"));
        return new(evals, table, grid.DefaultView, heat, curves, RadarProjector.Project(evals, defs), histograms, errors, performance);
    }
    private void RebuildItems()
    {
        var previous = SelectedItem?.Id; Items.Clear();
        if (dataset is null) return;
        var all = dataset.Queries.Select(q => new InspectorItem(q.Id, q.Text, string.Join("; ", dataset.JudgmentIndex.ForQuery(q.Id).Select(j => $"{j.DocumentId}: grade {j.Grade}, harmful {j.Harmful}")), q.ImagePath))
            .Concat(dataset.Examples.Select(e => new InspectorItem(e.Id, e.Text + (e.Text2 is null ? "" : "\n" + e.Text2), e.Label ?? e.GoldScore?.ToString() ?? e.ExpectedAnswer ?? "Unjudged", e.ImagePath)));
        foreach (var item in all.Where(i => string.IsNullOrEmpty(Filter) || i.Id.Contains(Filter, StringComparison.OrdinalIgnoreCase) || i.Input.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
            Items.Add(item);
        SelectedItem = Items.FirstOrDefault(i => i.Id == previous) ?? Items.FirstOrDefault();
    }
    private void UpdateInspector()
    {
        if (dataset is null || SelectedItem is null) { Inspector = null; return; }
        var table = new DataTable(); table.Columns.Add("Detail");
        for (int i = 0; i < Scenarios.Count; i++) table.Columns.Add($"{i + 1}: {Scenarios[i].Name}");
        var columns = new List<List<string>>();
        foreach (var scenario in Scenarios)
        {
            var model = FindResult(scenario);
            var lines = new List<string>();
            if (model is null) { columns.Add(["Missing run"]); continue; }
            if (model.Queries.FirstOrDefault(q => q.QueryId == SelectedItem.Id) is { } q)
            {
                lines.Add(q.Error is { } qe ? Redactor.Redact(qe) : $"Latency {q.LatencyMs:0.00} ms | threshold {scenario.Threshold?.ToString() ?? "none"} | K={scenario.K}");
                int retained = 0;
                foreach (var r in q.Results)
                {
                    var keep = (scenario.Threshold is null || r.Score >= scenario.Threshold) && retained < scenario.K;
                    if (keep) retained++;
                    var j = dataset.JudgmentIndex.Get(q.QueryId, r.DocumentId);
                    lines.Add($"{(keep ? "RETAINED" : "FILTERED")} {r.DocumentId}: {r.Score:0.0000} | grade {j?.Grade?.ToString() ?? "unjudged"} | harm {j?.Harmful?.ToString() ?? "unjudged"}\n{dataset.FindDocument(r.DocumentId)?.Text}");
                }
            }
            else if (model.Examples.FirstOrDefault(e => e.ExampleId == SelectedItem.Id) is { } e)
            {
                var classified = new MetricContext { Dataset = dataset, Result = model, Threshold = scenario.Threshold }.ClassifiedExamples().FirstOrDefault(x => x.Example.Id == e.ExampleId);
                lines.Add(e.Error is { } ee ? Redactor.Redact(ee) : $"Score {e.Score} | predicted {classified.Predicted ?? e.PredictedLabel ?? "n/a"} | {e.LatencyMs:0.00} ms\n{e.Output}");
            }
            else lines.Add("Missing prediction (not a negative/safe result)");
            columns.Add(lines);
        }
        for (int row = 0; row < columns.Select(c => c.Count).DefaultIfEmpty().Max(); row++)
            table.Rows.Add(new object[] { row == 0 ? "Summary" : "Rank " + row }.Concat(columns.Select(c => (object)(row < c.Count ? c[row] : ""))).ToArray());
        Inspector = table.DefaultView;
    }
    private void UpdateRanking()
    {
        if (RankingPolicy is null || evaluations.Count == 0) { Ranking = ""; return; }
        var result = RankingEngine.Rank(evaluations, RankingPolicy, metrics);
        Ranking = result.Formula + "\n" + result.MissingDataPolicy + "\nWeights: " + string.Join(", ", RankingPolicy.Weights.Select(w => $"{w.Key}={w.Value}")) +
            "\n" + string.Join("\n", result.Rows.Select(r => $"{r.Rank?.ToString() ?? "Excluded"}: {r.Scenario.Name} | {r.Score:0.000} {r.ExclusionReason}"));
    }
    private async Task OpenAsync()
    {
        if (!CanReplaceWorkspace() || dialogs.Open("Evaluation workspace|*.smeval") is not { } path) return;
        var known = Models.Select(m => m.Model).ToArray();
        var loaded = await Task.Run(() => WorkspaceService.Open(path, id => known.FirstOrDefault(m => m.Identity == id)), Token);
        analysisCancellation?.Cancel(); restoring = true;
        workspace = loaded.Workspace; workspacePath = path; dataset = loaded.Dataset;
        var savedPage = int.TryParse(workspace.View.ActiveView, out var savedIndex) && savedIndex is >= 0 and <= 5 ? savedIndex : 3;
        var savedWeights = new Dictionary<string, double>(workspace.View.RankingWeights);
        Scenarios.Clear(); foreach (var s in workspace.Scenarios) Scenarios.Add(s);
        notes = workspace.Notes; selectedScenario = null; reference = null;
        restoring = false;
        SetupDataset();
        ComparisonTab = int.TryParse(workspace.View.ComparatorTab, out var tab) ? tab : 0;
        if (workspace.View.RankingPolicyName == "Custom priorities" && savedWeights.Count > 0)
            RankingPolicies.Add(new RankingPolicy { Name = "Custom priorities", Weights = savedWeights });
        RankingPolicy = RankingPolicies.FirstOrDefault(p => p.Name == workspace.View.RankingPolicyName) ?? RankingPolicies.FirstOrDefault();
        SelectedScenario = Scenarios.FirstOrDefault(s => s.Id == workspace.View.FocusScenarioId) ??
            Scenarios.FirstOrDefault(s => s.ModelKey == workspace.View.FocusModelKey) ?? Scenarios.FirstOrDefault();
        Reference = Scenarios.FirstOrDefault(s => s.Id == workspace.ReferenceScenarioId) ?? Scenarios.FirstOrDefault();
        OnPropertyChanged(nameof(Notes)); Page = savedPage;
        MetricGrid = null; Curves = []; Histograms = []; Radar = null; Heatmap = []; comparison = null; evaluations = [];
        Summary = dataset is null ? "Cached results preserved. Restore the original dataset and reopen to calculate metrics." : "";
        await RecalculateAsync();
        Dirty = false;
        var notices = loaded.Warnings.Select(w => w.Message).ToList();
        if (workspace.Runs.Any(r => r.Config.MetricVersions.Count == 0))
            notices.Add("Legacy run has no metric version provenance. Analysis uses the installed definitions; re-evaluation is recommended.");
        Status = notices.Count > 0 ? string.Join("\n", notices) : "Workspace restored with exact dataset identity.";
    }
    private async Task SaveAsync(bool saveAs)
    {
        var path = saveAs || workspacePath is null ? dialogs.Save("Evaluation workspace|*.smeval", ".smeval") : workspacePath;
        if (path is null) return;
        workspace.View.SelectedMetricIds = [.. Metrics.Where(m => m.Selected).Select(m => m.Definition.Id)];
        var snapshot = WorkspaceSerializer.Deserialize(WorkspaceSerializer.Serialize(workspace));
        await Task.Run(() => WorkspaceService.Save(snapshot, path), Token);
        workspacePath = path; Dirty = false; Status = "Workspace saved: " + path;
    }
    private async Task ExportAsync()
    {
        await RecalculateAsync();
        if (comparison is null || evaluations.Count == 0) throw new InvalidOperationException("Load valid evaluation results before exporting.");
        if (IncludeExamples && !dialogs.Confirm("Include dataset text and model outputs in this export? They may contain sensitive data.")) return;
        var extension = ExportFormat switch { "CSV" => ".csv", "Text" => ".txt", "HTML" => ".html", _ => ".json" };
        if (dialogs.Save("Comparison report|*" + extension, extension) is not { } path) return;
        var context = new ExportContext { Workspace = workspace, Dataset = dataset, Evaluations = evaluations, Comparison = comparison };
        var options = new ExportOptions { IncludeExampleText = IncludeExamples };
        var format = ExportFormat;
        await Task.Run(() => AtomicFile.WriteAllText(path, format switch
        {
            "CSV" => ReportExporter.ToCsv(context), "Text" => ReportExporter.ToText(context),
            "HTML" => ReportExporter.ToHtml(context, options), _ => ReportExporter.ToJson(context, options)
        }), Token);
        Status = "Export saved: " + path;
    }
    public void Dispose() { runCancellation?.Cancel(); analysisCancellation?.Cancel(); }
}
