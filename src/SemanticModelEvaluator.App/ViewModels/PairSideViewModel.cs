using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SemanticModelEvaluator.App.Services;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.EvalKit;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Pairing;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.EvalKit;

namespace SemanticModelEvaluator.App.ViewModels;

/// <summary>Where one side's results come from. Loading captured results never invokes a model.</summary>
public enum SideSource { NewEvaluation, EvalKitLocalFolder, EvalKitCloud, EvalKitCachedBundle, SavedRun }

public sealed record SourceOption(SideSource Value, string Label, string Explanation);

/// <summary>A captured run that can be loaded for one side (EvalKit metrics envelope or a run saved by this app).</summary>
public sealed class CapturedRunChoice
{
    public required string Label { get; init; }
    public required ModelConfiguration Configuration { get; init; }
    public required DatasetReference RunDataset { get; init; }
    public required ResultOrigin Origin { get; init; }
    public required string SourceDescription { get; init; }
    public EvalKitRun? EvalKit { get; init; }
    public ModelRunResult? Saved { get; init; }
    public string? RunId { get; init; }
    public DateTimeOffset? RunAt { get; init; }
    public int? CachedDepth { get; init; }
    public string? Classification { get; init; }
    public override string ToString() => Label;
}

public sealed record BundleChoice(string Folder, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One side (Model A or Model B) of a two-model comparison.</summary>
public sealed class PairSideViewModel : ObservableObject
{
    private readonly ProviderRegistry providers;
    private readonly EvalKitCloudService cloud;
    private readonly IUserDialogs dialogs;
    private readonly Action<string> report;
    private readonly Func<CancellationToken> token;
    private SideSource source = SideSource.NewEvaluation;
    private IModelProvider? provider;
    private ModelDescriptor? model;
    private CapturedRunChoice? run;
    private EvalKitCloudJob? job;
    private BundleChoice? bundle;
    private string connection = "Choose a provider.";
    private string validation = "";
    private string search = "";
    private string experiment = "";
    private bool sensitive;
    private bool useThreshold;
    private double threshold;
    private double min, max = 1;
    private ModelRunResult? loadedResult;

    public PairSideViewModel(string title, string color, ProviderRegistry providers, EvalKitCloudService cloud, IUserDialogs dialogs,
        Func<Func<Task>, IAsyncRelayCommand> command, Action<string> report, Func<CancellationToken> token)
    {
        Title = title; Color = color;
        this.providers = providers; this.cloud = cloud; this.dialogs = dialogs; this.report = report; this.token = token;
        Providers = [.. providers.Providers];
        ValidateCommand = command(ValidateAsync);
        SearchCommand = command(SearchAsync);
        PrepareCommand = command(PrepareAsync);
        BrowseFolderCommand = command(BrowseFolderAsync);
        ListJobsCommand = command(ListJobsAsync);
        FetchJobCommand = command(FetchJobAsync);
        RefreshBundlesCommand = command(RefreshBundlesAsync);
        OpenSavedRunCommand = command(OpenSavedRunAsync);
    }

    public event Action? Changed;
    public event Action? ThresholdChanged;
    public string Title { get; }
    public string Color { get; }
    public IReadOnlyList<IModelProvider> Providers { get; }
    public ObservableCollection<ModelDescriptor> Models { get; } = [];
    public ObservableCollection<CapturedRunChoice> Runs { get; } = [];
    public ObservableCollection<EvalKitCloudJob> Jobs { get; } = [];
    public ObservableCollection<BundleChoice> Bundles { get; } = [];
    public IAsyncRelayCommand ValidateCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand BrowseFolderCommand { get; }
    public IAsyncRelayCommand ListJobsCommand { get; }
    public IAsyncRelayCommand FetchJobCommand { get; }
    public IAsyncRelayCommand RefreshBundlesCommand { get; }
    public IAsyncRelayCommand OpenSavedRunCommand { get; }

    public static IReadOnlyList<SourceOption> SourceOptions { get; } =
    [
        new(SideSource.NewEvaluation, "Model provider — invoke for a new evaluation", "Runs the model on the selected dataset. Remote providers receive dataset text after you confirm."),
        new(SideSource.EvalKitLocalFolder, "EvalKit run-output folder (local)", "Loads captured scores from a complete EvalKit output folder (*.metrics.json + query results). No model is invoked."),
        new(SideSource.EvalKitCloud, "EvalKit cloud results (authorized)", "Lists and downloads hash-verified result bundles with your own Azure CLI sign-in. No model is invoked."),
        new(SideSource.EvalKitCachedBundle, "Cached EvalKit bundle", "Reuses a bundle already downloaded and verified by this app or EvalKit.Viewer. No network, no model invocation."),
        new(SideSource.SavedRun, "Saved run from this app", "Loads a run JSON previously produced by this application. No model is invoked.")
    ];

    public SideSource Source
    {
        get => source;
        set
        {
            if (!SetProperty(ref source, value)) return;
            Run = null; Runs.Clear(); Jobs.Clear(); loadedResult = null;
            useThreshold = false; OnPropertyChanged(nameof(UseThreshold)); SetScores(null);
            foreach (var n in new[] { nameof(IsProviderSource), nameof(IsFolderSource), nameof(IsCloudSource), nameof(IsBundleSource), nameof(IsSavedSource), nameof(IsCapturedSource), nameof(SourceExplanation), nameof(IsPsapiLive) })
                OnPropertyChanged(n);
            if (value == SideSource.EvalKitCachedBundle) _ = RefreshBundlesCommand.ExecuteAsync(null);
            RaiseChanged();
        }
    }
    public string SourceExplanation => SourceOptions.First(o => o.Value == source).Explanation;
    public bool IsProviderSource => source == SideSource.NewEvaluation;
    public bool IsCapturedSource => source != SideSource.NewEvaluation;
    public bool IsFolderSource => source == SideSource.EvalKitLocalFolder;
    public bool IsCloudSource => source == SideSource.EvalKitCloud;
    public bool IsBundleSource => source == SideSource.EvalKitCachedBundle;
    public bool IsSavedSource => source == SideSource.SavedRun;

    public IModelProvider? Provider
    {
        get => provider;
        set
        {
            if (!SetProperty(ref provider, value)) return;
            Models.Clear(); Model = null; Validation = "";
            if (value is not null) _ = LoadProviderAsync(value);
            OnPropertyChanged(nameof(IsHuggingFace));
        }
    }
    public bool IsHuggingFace => provider?.Id == HuggingFaceProvider.ProviderId;

    public ModelDescriptor? Model
    {
        get => model;
        set
        {
            if (!SetProperty(ref model, value)) return;
            useThreshold = false; OnPropertyChanged(nameof(UseThreshold)); SetScores(null);
            Validation = ""; OnPropertyChanged(nameof(ModelDetail));
            if (value is { Identity.ProviderId: PsapiProvider.ProviderId, LocalPath: { Length: > 0 } folder }) { UseCapturedBundle(folder, value.DisplayName); return; }
            OnPropertyChanged(nameof(IsPsapiLive));
            RaiseChanged();
        }
    }

    /// <summary>The PSAPI catalog model selected for a live local EvalKit-backend run (null for every other selection).</summary>
    public PsapiModelSpec? PsapiSpec => source == SideSource.NewEvaluation && model is { Identity.ProviderId: PsapiProvider.ProviderId, LocalPath: null or "" }
        ? PsapiProvider.Find(model.Identity.ModelId) : null;
    public bool IsPsapiLive => PsapiSpec is not null;

    /// <summary>A captured PSAPI bundle (cloud result) was chosen: compare its recorded scores; no model is invoked.</summary>
    private void UseCapturedBundle(string folder, string label)
    {
        source = SideSource.EvalKitCachedBundle;
        OnPropertyChanged(nameof(Source));
        foreach (var n in new[] { nameof(IsProviderSource), nameof(IsFolderSource), nameof(IsCloudSource), nameof(IsBundleSource), nameof(IsSavedSource), nameof(IsCapturedSource), nameof(SourceExplanation), nameof(IsPsapiLive) })
            OnPropertyChanged(n);
        Bundles.Clear(); Bundles.Add(new BundleChoice(folder, label));
        Bundle = Bundles[0];
        report($"{label}: captured PSAPI results from a verified EvalKit bundle were loaded. No model is invoked.");
        RaiseChanged();
    }

    public CapturedRunChoice? Run
    {
        get => run;
        set
        {
            if (!SetProperty(ref run, value)) return;
            loadedResult = null; useThreshold = false; OnPropertyChanged(nameof(UseThreshold));
            OnPropertyChanged(nameof(ModelDetail)); RaiseChanged();
        }
    }
    public EvalKitCloudJob? Job { get => job; set => SetProperty(ref job, value); }
    public BundleChoice? Bundle
    {
        get => bundle;
        set { if (SetProperty(ref bundle, value) && value is not null) AddRunsFromFolder(value.Folder, ResultOrigin.LoadedEvalKitCloud, EvalKitCloudService.ClassificationFor(value.Folder), value.Label); }
    }
    public string Search { get => search; set => SetProperty(ref search, value); }
    public string Experiment { get => experiment; set => SetProperty(ref experiment, value); }
    /// <summary>Explicit opt-in. Access to the sensitive container is checked by Azure; there is never a fallback to another identity or container.</summary>
    public bool Sensitive { get => sensitive; set { if (SetProperty(ref sensitive, value)) Jobs.Clear(); } }
    public string Connection { get => connection; private set => SetProperty(ref connection, value); }
    public string Validation { get => validation; set => SetProperty(ref validation, value); }

    /// <summary>Provider-neutral configuration used for pairing rules, the report and reproducibility.</summary>
    public ModelConfiguration? Configuration => source == SideSource.NewEvaluation
        ? model is null ? null : ModelConfiguration.From(model)
        : run?.Configuration;

    public string ModelDetail
    {
        get
        {
            var c = Configuration;
            if (c is null) return "Nothing selected.";
            var lines = new List<string> { $"Provider: {c.ProviderName}", $"Model / deployment: {c.ModelId}", $"Version / revision: {c.Revision ?? "unknown"}" };
            if (c.Endpoint is not null) lines.Add("Endpoint: " + Redactor.Redact(c.Endpoint));
            foreach (var (k, v) in c.Parameters) lines.Add($"{k}: {v}");
            if (source == SideSource.NewEvaluation && model is not null) lines.Add($"Readiness: {model.Readiness} — {model.ReadinessDetail}");
            if (run is not null)
            {
                lines.Add("Captured run: " + (run.RunId ?? "?") + (run.RunAt is { } at ? " · " + at.ToString("u") : ""));
                lines.Add($"Run dataset: {run.RunDataset.Name} v{run.RunDataset.Version} · hash {Short(run.RunDataset.ContentHash)}");
                if (run.Classification is not null) lines.Add("Handling: " + run.Classification);
                if (run.EvalKit is { } meta) lines.Add("EvalKit: " + meta.BatchMetadata);
                if (run.EvalKit is { Warnings.Count: > 0 } ek) lines.AddRange(ek.Warnings.Select(w => "Warning: " + w));
            }
            return string.Join("\n", lines);
        }
    }

    public bool UseThreshold
    {
        get => useThreshold;
        set { if (SetProperty(ref useThreshold, value)) { OnPropertyChanged(nameof(ThresholdText)); ThresholdChanged?.Invoke(); } }
    }
    /// <summary>Score threshold for this side only (providers use different score scales). Changing it recalculates from captured scores; no model is re-run.</summary>
    public double Threshold
    {
        get => threshold;
        set
        {
            var v = Math.Clamp(value, min, max);
            if (Math.Abs(v - threshold) < 1e-12) return;
            threshold = v; OnPropertyChanged(); OnPropertyChanged(nameof(ThresholdText));
            if (!useThreshold) { useThreshold = true; OnPropertyChanged(nameof(UseThreshold)); }
            ThresholdChanged?.Invoke();
        }
    }
    public string ThresholdText
    {
        get => useThreshold ? threshold.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) : "";
        set
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
                Threshold = v;
            else if (string.IsNullOrWhiteSpace(value)) UseThreshold = false;
        }
    }
    public double Min { get => min; private set => SetProperty(ref min, value); }
    public double Max { get => max; private set => SetProperty(ref max, value); }
    public double? EffectiveThreshold => useThreshold ? threshold : null;
    public string ScoreRangeText => scoresAreRanks
        ? $"RANK CUT-OFF: some or all results have positions but no score, so −position is used (range {min:0.###} – {max:0.###}). A threshold of −5 keeps ranks 1–5 of rank-only rows; it is not comparable to a score threshold."
        : $"Score range {min:0.###} – {max:0.###} (this model's own scale)";
    private bool scoresAreRanks;

    public void SetScores(ModelRunResult? result)
    {
        scoresAreRanks = result?.ScoresIncludeRanks == true;
        if (result is null || !result.AllScores().Any()) { Min = 0; Max = 1; OnPropertyChanged(nameof(ScoreRangeText)); return; }
        var (lo, hi) = ThresholdEngine.ScoreRange([result]);
        Min = lo; Max = hi <= lo ? lo + 1 : hi;
        if (threshold < Min || threshold > Max) { threshold = Min; OnPropertyChanged(nameof(Threshold)); OnPropertyChanged(nameof(ThresholdText)); }
        OnPropertyChanged(nameof(ScoreRangeText));
    }

    private void RaiseChanged() { OnPropertyChanged(nameof(Configuration)); Changed?.Invoke(); }
    private static string Short(string? h) => string.IsNullOrEmpty(h) ? "unknown" : h.Length > 20 ? h[..20] + "…" : h;

    private async Task LoadProviderAsync(IModelProvider p)
    {
        try
        {
            var health = await p.CheckHealthAsync(token());
            Connection = Redactor.Redact($"{p.DisplayName}: {health.Status}{(string.IsNullOrEmpty(health.Detail) ? "" : " — " + health.Detail)}");
            var found = await Task.Run(() => p.DiscoverAsync(new ModelQuery(), token()));
            if (provider != p) return;
            var listed = SemanticSearchOnly(p, found);
            foreach (var m in listed) Models.Add(m);
            if (listed.Count < found.Count) Connection += p.Id == PsapiProvider.ProviderId
                ? "\nCaptured runs are not executable models. To inspect them, choose the Cached EvalKit bundle source."
                : $"\n{found.Count - listed.Count} non-semantic-search model(s) hidden (generation, classification or reranking).";
            if (found.Count == 0) Connection += p.Id switch
            {
                FoundryProvider.ProviderId => "\nNo deployments configured. Add one in Settings → Provider settings → Microsoft AI Foundry.",
                PsapiProvider.ProviderId => "\nNo PSAPI models found.",
                HuggingFaceProvider.ProviderId => "\nNo cached Hugging Face models. Search the hub below.",
                _ => "\nNo models found."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Connection = p.DisplayName + ": " + Redactor.Redact(ex.Message); }
    }

    private async Task ValidateAsync()
    {
        if (source != SideSource.NewEvaluation)
        {
            Validation = run is null ? "Select a captured run." : run.EvalKit is { } ek
                ? $"Captured EvalKit run. Dataset {ek.Dataset.Name ?? "?"} v{ek.Dataset.Version ?? "?"}; {ek.QueryCount} queries, {ek.JudgedCount} judged, {ek.FailedQueryCount} failed, {ek.ZeroResultCount} empty." +
                  (ek.Warnings.Count > 0 ? "\n" + string.Join("\n", ek.Warnings) : "")
                : "Saved run from this app.";
            return;
        }
        if (provider is null || model is null) { Validation = "Select a provider and a model."; return; }
        var r = await provider.ValidateAsync(model, token());
        Validation = (r.IsValid ? "Valid. " : "Not valid. ") + Redactor.Redact(r.ToString());
    }

    private async Task SearchAsync()
    {
        var p = provider;
        if (p is null) return;
        if (p.UsesNetwork && !dialogs.Confirm($"Send the search text to {p.DisplayName}? Dataset text is not sent.")) return;
        var query = Search;
        var found = await Task.Run(() => p.DiscoverAsync(new ModelQuery(query, AllowNetwork: true), token()));
        if (!ReferenceEquals(provider, p)) { report("Provider changed during search; results discarded."); return; }
        var listed = SemanticSearchOnly(p, found);
        Models.Clear(); foreach (var m in listed) Models.Add(m);
        report($"{listed.Count} semantic-search model(s) found" + (listed.Count < found.Count ? $" ({found.Count - listed.Count} other model(s) hidden)" : "") +
               ". Check license and compatibility before downloading.");
    }

    private static List<ModelDescriptor> SemanticSearchOnly(IModelProvider p, IReadOnlyList<ModelDescriptor> found) =>
        p.Id == HuggingFaceProvider.ProviderId ? [.. found.Where(HuggingFaceProvider.IsSemanticSearchModel)]
        : p.Id == PsapiProvider.ProviderId ? [.. found.Where(m => string.IsNullOrEmpty(m.LocalPath))] : [.. found];

    private async Task PrepareAsync()
    {
        if (model is null || provider is not IAcquiringModelProvider acquiring || model.Readiness is not (ReadinessState.NeedsDownload or ReadinessState.RequiresAuthentication))
        { report("The selected model does not need preparation."); return; }
        if (!dialogs.Confirm($"Download '{model.DisplayName}' model files? Check the license. Dataset content is not uploaded.")) return;
        var progress = new Progress<ProgressInfo>(p => report(p.Message + (p.Fraction is { } f ? $" {f:P0}" : "")));
        var ready = await acquiring.AcquireAsync(model, progress, token());
        var v = await acquiring.ValidateAsync(ready, token());
        if (!v.IsValid) throw new InvalidDataException(v.ToString());
        var i = Models.IndexOf(model); if (i >= 0) Models[i] = ready; else Models.Add(ready);
        Model = ready; report("Model downloaded and validated.");
    }

    private async Task BrowseFolderAsync()
    {
        if (dialogs.Folder() is not { } folder) return;
        var classification = File.Exists(Path.Combine(folder, ".smeval-source.json")) ? EvalKitCloudService.ClassificationFor(folder) : null;
        await Task.Run(() => AddRunsFromFolder(folder, ResultOrigin.LoadedEvalKitLocal, classification, folder));
    }

    private async Task ListJobsAsync()
    {
        var s = sensitive ? ResultSensitivity.Sensitive : ResultSensitivity.NonSensitive;
        var (account, container) = cloud.Location(s);
        if (!dialogs.Confirm($"List EvalKit results in {account}/{container} using your Azure CLI sign-in (az login)?" +
            (sensitive ? "\n\nThis is the SENSITIVE container. Access is checked by Azure; there is no fallback to another identity or container." : ""))) return;
        Jobs.Clear();
        if (string.IsNullOrWhiteSpace(Experiment))
        {
            var experiments = await cloud.ListExperimentsAsync(s, token());
            report(experiments.Count == 0 ? "No experiments visible to your account." : "Experiments: " + string.Join(", ", experiments) + "\nEnter one and list again.");
            return;
        }
        foreach (var j in await cloud.ListJobsAsync(Experiment.Trim(), s, token())) Jobs.Add(j);
        report($"{Jobs.Count} job(s) with a result manifest in '{Experiment}'. Select one and download one bundle; bulk download is not offered.");
    }

    private async Task FetchJobAsync()
    {
        if (job is null) { report("Select a job first."); return; }
        var progress = new Progress<string>(report);
        var folder = await cloud.FetchAsync(job, progress, token());
        AddRunsFromFolder(folder, ResultOrigin.LoadedEvalKitCloud, EvalKitCloudService.ClassificationFor(folder), job.ToString());
        report("Bundle verified (size + SHA-256) and cached: " + folder);
    }

    private async Task RefreshBundlesAsync()
    {
        var found = await Task.Run(() => cloud.ListCachedBundles());
        Bundles.Clear(); foreach (var (f, l) in found) Bundles.Add(new(f, l));
        report(found.Count == 0 ? "No cached EvalKit bundles yet." : $"{found.Count} cached bundle(s).");
    }

    private async Task OpenSavedRunAsync()
    {
        if (dialogs.Open("Saved run|*.json") is not { } path) return;
        var saved = await Task.Run(() => JsonSerializer.Deserialize<EvaluationRun>(File.ReadAllText(path), WorkspaceSerializer.Options))
            ?? throw new InvalidDataException("Not a saved run.");
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Runs.Clear();
            foreach (var m in saved.Models)
                Runs.Add(new CapturedRunChoice
                {
                    Label = $"{m.DisplayName} · {m.Status} · run {saved.RunId}",
                    Configuration = ModelConfiguration.From(m, providers.Find(m.Model.ProviderId)?.DisplayName ?? m.Model.ProviderId),
                    RunDataset = saved.Dataset, Origin = ResultOrigin.LoadedWorkspace, SourceDescription = "Saved run " + Path.GetFileName(path),
                    Saved = m, RunId = saved.RunId, RunAt = saved.StartedAt, CachedDepth = saved.Config.RetainDepth
                });
            Run = Runs.FirstOrDefault();
        });
    }

    /// <summary>Adds every EvalKit metrics envelope in a folder as a selectable run. Invalid envelopes are reported, not hidden.</summary>
    public void AddRunsFromFolder(string folder, ResultOrigin origin, string? classification, string description)
    {
        var manifestPath = Path.Combine(folder, "result-manifest.json");
        if (File.Exists(manifestPath))
        {
            try { EvalKitCloudService.VerifyBundle(folder); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => { Runs.Clear(); Run = null; report("Bundle rejected: " + Redactor.Redact(ex.Message)); });
                return;
            }
        }
        var manifest = File.Exists(manifestPath) ? JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject : null;
        var choices = new List<CapturedRunChoice>(); var errors = new List<string>();
        foreach (var metrics in EvalKitFormat.FindMetricsFiles(folder))
        {
            try
            {
                var ek = EvalKitFormat.ReadRun(metrics, manifest, classification);
                var id = ek.Identity;
                choices.Add(new CapturedRunChoice
                {
                    Label = $"{ek.ModelLabel} · {ek.RevisionLabel} · {ek.Dataset.Name ?? "?"} v{ek.Dataset.Version ?? "?"} · {Path.GetFileName(metrics)}",
                    Configuration = new ModelConfiguration(id.ProviderId, ek.ProviderLabel, id.ModelId, ek.ModelLabel, id.Revision, null,
                        new SortedDictionary<string, string>(StringComparer.Ordinal)),
                    RunDataset = RunDatasetOf(ek), Origin = origin, SourceDescription = description + " — " + ek.BatchMetadata, EvalKit = ek, RunId = ek.RunId,
                    RunAt = ek.CreatedAt, CachedDepth = ek.MaxResultsPerQuery, Classification = ek.Classification
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException) { errors.Add(Redactor.Redact(ex.Message)); }
        }
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Runs.Clear(); foreach (var c in choices) Runs.Add(c);
            Run = Runs.FirstOrDefault();
            report(choices.Count == 0 ? "No valid EvalKit metrics envelopes found." + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : "")
                : $"{choices.Count} captured run(s) found." + (errors.Count > 0 ? "\nSkipped: " + string.Join("\n", errors) : ""));
        });
    }

    public static DatasetReference RunDatasetOf(EvalKitRun ek) =>
        new(ek.Dataset.Name ?? "unknown", ek.Dataset.Name ?? "unknown", ek.Dataset.Version ?? "?", EvalTask.Retrieval, null, ek.Dataset.PayloadHash ?? "");

    /// <summary>
    /// Reads captured scores for the selected run (cached after the first read). Never invokes a model.
    /// Expected query ids (from the exact matching dataset) let queries with no result rows count as empty.
    /// </summary>
    public ModelRunResult? LoadCaptured(int retainDepth, CancellationToken ct = default, IReadOnlyCollection<string>? expectedQueryIds = null)
    {
        if (run is null) return null;
        if (loadedResult is not null && loadedWithExpectedIds == (expectedQueryIds is not null)) return loadedResult;
        loadedResult = run.Saved ?? (run.EvalKit is { } ek ? EvalKitFormat.ReadResults(ek, Math.Max(retainDepth, 1000), ct, expectedQueryIds) : null);
        loadedWithExpectedIds = expectedQueryIds is not null;
        return loadedResult;
    }
    private bool loadedWithExpectedIds;

    public void SelectProviderModel(string providerId, ModelDescriptor descriptor)
    {
        Source = SideSource.NewEvaluation;
        provider = providers.Find(providerId); OnPropertyChanged(nameof(Provider));
        Models.Clear(); Models.Add(descriptor); Model = descriptor;
    }
}
