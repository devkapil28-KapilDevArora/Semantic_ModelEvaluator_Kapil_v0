using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SemanticModelEvaluator.App.Services;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.EvalKit;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers.Datasets;
using SemanticModelEvaluator.Providers.EvalKit;

namespace SemanticModelEvaluator.App.ViewModels;

public sealed record DatasetCard(string Name, DatasetSource Source, string Version, string Task, string Modality,
    string Examples, string Judgments, string Access, string Approval, string Cache, string? Manifest = null, EvalKitDatasetEntry? Internal = null);

public sealed class DatasetColumnChoice(string column, ColumnRole role) : ObservableObject
{
    public string Column { get; } = column;
    private ColumnRole role = role;
    public ColumnRole Role { get => role; set => SetProperty(ref role, value); }
}

public sealed class DatasetHubViewModel : ObservableObject, IDisposable
{
    private readonly HuggingFaceDatasets hub;
    private readonly EvalKitDatasetCatalog catalog;
    private readonly EvalKitCli cli;
    private readonly WorkbenchViewModel workbench;
    private readonly PairComparisonViewModel pair;
    private readonly SettingsStore settings;
    private readonly NetworkPolicy network;
    private readonly IUserDialogs dialogs;
    private CancellationTokenSource? operation;
    private bool busy;
    private int sourceIndex;
    private string filter = "", repository = "BeIR/scifact", revision = "main", status = "Choose a public starter, or list your authorized internal catalog. Downloaded datasets run locally; no dataset content is sent to Hugging Face.";
    private DatasetCard? selected;
    private HubDatasetInfo? info;
    private HubDatasetFile? selectedFile;
    private HubDatasetConfiguration? configuration;
    private string? preparedPath;
    private HubDatasetInfo? preparedHub;
    private string? preparedFile;
    private HubDatasetConfiguration? preparedConfiguration;
    private TableKind kind = TableKind.Queries;
    private string details = "";
    private DataView? preview;

    public DatasetHubViewModel(HuggingFaceDatasets hub, EvalKitDatasetCatalog catalog, EvalKitCli cli,
        WorkbenchViewModel workbench, PairComparisonViewModel pair, SettingsStore settings, NetworkPolicy network, IUserDialogs dialogs)
    {
        this.hub = hub; this.catalog = catalog; this.cli = cli; this.workbench = workbench; this.pair = pair;
        this.settings = settings; this.network = network; this.dialogs = dialogs;
        SearchCommand = Command(SearchAsync);
        InspectCommand = Command(InspectAsync);
        DownloadCommand = Command(DownloadAsync);
        ImportStarterCommand = Command(ImportStarterAsync);
        LocalTableCommand = Command(LocalTableAsync);
        RefreshCommand = Command(RefreshLocalAsync);
        ListInternalCommand = Command(ListInternalAsync);
        FetchMatchingCommand = Command(FetchMatchingAsync);
        FetchNewCommand = Command(FetchNewAsync);
        PreviewCommand = Command(PreviewAsync);
        AddInputCommand = Command(AddInputAsync);
        BuildCommand = Command(BuildAsync);
        UseCommand = Command(async () => { await workbench.LoadDatasetAsync(RequireManifest()); workbench.Page = 2; });
        UsePairCommand = Command(async () => { await pair.LoadDatasetAsync(RequireManifest()); workbench.TabIndex = 0; });
        RemoveDownloadCommand = Command(() =>
        {
            if (info is null || SelectedFile is null) throw new InvalidOperationException("Select a pinned Hub file.");
            var path = hub.FileCachePath(info, SelectedFile);
            if (dialogs.Confirm("Remove only this downloaded file and its partial download? Normalized datasets, model caches, internal caches and workspaces are kept."))
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".partial")) File.Delete(path + ".partial");
                Status = "Selected public download removed.";
            }
            return System.Threading.Tasks.Task.CompletedTask;
        });
        CancelCommand = new RelayCommand(() => operation?.Cancel());
        ClearInputsCommand = new RelayCommand(() => Inputs.Clear());
    }

    public ObservableCollection<DatasetCard> Cards { get; } = [];
    public IEnumerable<DatasetCard> VisibleCards => Cards.Where(c =>
        (SourceIndex == 0 || c.Source == (SourceIndex == 1 ? DatasetSource.HuggingFace : SourceIndex == 2 ? DatasetSource.MicrosoftInternal : DatasetSource.Local)) &&
        (Filter.Length == 0 || c.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))).OrderBy(c => c.Name).ThenBy(c => c.Version);
    public ObservableCollection<HubDatasetFile> Files { get; } = [];
    public ObservableCollection<HubDatasetConfiguration> Configurations { get; } = [];
    public ObservableCollection<DatasetColumnChoice> Columns { get; } = [];
    public ObservableCollection<DatasetImportInput> Inputs { get; } = [];
    public IReadOnlyList<PublicDatasetRecipe> Starters => PublicDatasetStarters.All;
    public PublicDatasetRecipe Starter { get; set; } = PublicDatasetStarters.All[0];
    public IReadOnlyList<GenoaDatasetFamily> InternalInventory => GenoaDatasetInventory.Families;
    public string InternalSetup => GenoaDatasetInventory.Setup;
    public IReadOnlyList<TableKind> TableKinds { get; } = Enum.GetValues<TableKind>();
    public IReadOnlyList<ColumnRole> ColumnRoles { get; } = Enum.GetValues<ColumnRole>();
    public IReadOnlyList<EvalTask> Tasks { get; } = Enum.GetValues<EvalTask>();
    public IReadOnlyList<EvalKitDatasetContainer> Containers => EvalKitDatasetContainer.Known;
    public EvalKitDatasetContainer Container { get; set; } = EvalKitDatasetContainer.Known.First();
    public string PayloadHash { get; set; } = "";
    public string DatasetName { get; set; } = "Imported benchmark subset";
    public EvalTask Task { get; set; } = EvalTask.Retrieval;
    public int RowsPerFile { get; set; } = 1000;
    public int StorageMegabytes { get; set; } = 128;
    public bool LicenseAccepted { get; set; }
    public bool MappingConfirmed { get; set; }
    public bool CaptionPairs { get; set; }
    public bool ImageQueries { get; set; }
    public bool Busy { get => busy; private set { SetProperty(ref busy, value); OnPropertyChanged(nameof(Idle)); } }
    public bool Idle => !Busy;
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Details { get => details; private set => SetProperty(ref details, value); }
    public DataView? Preview { get => preview; private set => SetProperty(ref preview, value); }
    public int SourceIndex { get => sourceIndex; set { if (SetProperty(ref sourceIndex, value)) OnPropertyChanged(nameof(VisibleCards)); } }
    public string Filter { get => filter; set { if (SetProperty(ref filter, value)) OnPropertyChanged(nameof(VisibleCards)); } }
    public string Repository
    {
        get => repository;
        set
        {
            if (!SetProperty(ref repository, value)) return;
            info = null; Files.Clear(); Configurations.Clear(); SelectedFile = null; Configuration = null;
            Revision = "main";
            LicenseAccepted = false; OnPropertyChanged(nameof(LicenseAccepted));
        }
    }
    public string Revision { get => revision; set => SetProperty(ref revision, value); }
    public HubDatasetFile? SelectedFile { get => selectedFile; set => SetProperty(ref selectedFile, value); }
    public HubDatasetConfiguration? Configuration
    {
        get => configuration;
        set { if (SetProperty(ref configuration, value) && value is not null) Details = value.Schema + "\nFile patterns: " + string.Join(", ", value.Patterns); }
    }
    public DatasetCard? Selected
    {
        get => selected;
        set { if (SetProperty(ref selected, value) && value?.Source == DatasetSource.HuggingFace && value.Manifest is null) Repository = value.Name; }
    }
    public TableKind Kind
    {
        get => kind;
        set { if (SetProperty(ref kind, value)) Suggest(); }
    }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand InspectCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand ImportStarterCommand { get; }
    public IAsyncRelayCommand LocalTableCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ListInternalCommand { get; }
    public IAsyncRelayCommand FetchMatchingCommand { get; }
    public IAsyncRelayCommand FetchNewCommand { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand AddInputCommand { get; }
    public IAsyncRelayCommand BuildCommand { get; }
    public IAsyncRelayCommand UseCommand { get; }
    public IAsyncRelayCommand UsePairCommand { get; }
    public IAsyncRelayCommand RemoveDownloadCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearInputsCommand { get; }

    private IAsyncRelayCommand Command(Func<Task> action) => new AsyncRelayCommand(async () =>
    {
        if (Busy || workbench.Busy || pair.Busy) { Status = "Wait for the current evaluation to finish or cancel it first."; return; }
        Busy = true;
        using var cts = new CancellationTokenSource();
        operation = cts;
        try { await action(); }
        catch (OperationCanceledException) { Status = "Cancelled. Verified cached files are kept; partial public downloads can be resumed with Download / Retry."; }
        catch (Exception ex) { Status = "Dataset operation failed: " + Redactor.Redact(ex.Message); }
        finally { operation = null; Busy = false; OnPropertyChanged(nameof(VisibleCards)); }
    });
    private CancellationToken Token => operation?.Token ?? CancellationToken.None;
    private void AddCard(DatasetCard card)
    {
        var old = Cards.FirstOrDefault(c => c.Name == card.Name && c.Version == card.Version && c.Source == card.Source);
        if (old is not null) Cards.Remove(old);
        Cards.Add(card); Selected = card; OnPropertyChanged(nameof(VisibleCards));
    }
    private async Task SearchAsync()
    {
        if (!dialogs.Confirm("Send these public dataset search terms to huggingface.co? Do not enter internal identifiers or dataset content.")) return;
        foreach (var id in await hub.SearchAsync(Repository, Token))
            AddCard(new(id, DatasetSource.HuggingFace, "Resolve revision", "Inspect schema", "Unknown", "Unknown", "Unknown", "Inspect access/license", "Not assessed", "Metadata only"));
        Status = "Search returned at most 20 repositories. Inspect a chosen repository and revision; nothing was downloaded.";
    }
    private async Task InspectAsync()
    {
        info = network.OfflineMode ? hub.ReadCached(Repository, Revision) : await hub.InspectAsync(Repository, Revision, Token);
        Revision = info.Revision;
        LicenseAccepted = false; OnPropertyChanged(nameof(LicenseAccepted));
        Files.Clear(); foreach (var file in info.Files.Where(f => f.IsTable)) Files.Add(file);
        Configurations.Clear(); foreach (var config in info.Configurations) Configurations.Add(config);
        Configuration = Configurations.FirstOrDefault(); SelectedFile = Files.FirstOrDefault();
        Details = $"Repository: {info.Repository}\nPinned revision: {info.Revision}\nLicense: {info.License}\n{info.Access}\n" +
            $"Static tables: {Files.Count}; configurations/splits: {Configurations.Count}. Unknown metadata stays unknown. No scripts will run.";
        Status = "Metadata resolved. Select the intended configuration/split and matching static file; review license and byte budget.";
    }
    private async Task DownloadAsync()
    {
        var metadata = info ?? throw new InvalidOperationException("Inspect the repository first.");
        var file = SelectedFile ?? throw new InvalidOperationException("Select a static data file.");
        if (StorageMegabytes is < 1 or > 2048) throw new InvalidOperationException("Storage budget must be 1-2048 MB per file.");
        var path = await hub.DownloadAsync(metadata, file, StorageMegabytes * 1048576L, LicenseAccepted, new Progress<string>(s => Status = s), Token);
        preparedHub = metadata; preparedFile = file.Path; preparedConfiguration = Configuration;
        await PrepareAsync(path);
    }
    private async Task ImportStarterAsync()
    {
        if (StorageMegabytes is < 1 or > 2048) throw new InvalidOperationException("Set a download budget of 1-2048 MB.");
        long budget = StorageMegabytes * 1048576L;
        Status = "Resolving public starter files, licenses and immutable revisions...";
        var plan = await PublicDatasetStarters.InspectAsync(hub, Starter, budget, Token);
        Details = plan.Summary;
        if (!dialogs.Confirm(plan.Summary + "\n\nReview the linked dataset cards and original asset rights. Accept these license terms and explicit mappings, and download this starter?")) return;
        var root = Path.Combine(AppPaths.Root, "datasets", "public", "normalized", Guid.NewGuid().ToString("N"));
        var path = await PublicDatasetStarters.ImportAsync(hub, plan, budget, true, root, new Progress<string>(s => Status = s), Token);
        settings.Update(s => SettingsStore.AddRecent(s.RecentDatasets, path));
        SourceIndex = 0; Filter = "";
        AddLocalCard(path);
        Status = "Starter is ready and selected. Choose 'Use in two-model comparison' or 'Add to evaluation'. No manual column mapping is needed.";
    }
    private async Task LocalTableAsync()
    {
        if (dialogs.Open("Static dataset table|*.parquet;*.csv;*.tsv;*.json;*.jsonl") is not { } path) return;
        preparedHub = null; preparedFile = null; preparedConfiguration = null;
        await PrepareAsync(path);
    }
    private async Task PrepareAsync(string path)
    {
        preparedPath = path;
        Columns.Clear();
        foreach (var column in await DatasetTableReader.ColumnsAsync(path, Token)) Columns.Add(new(column, ColumnRole.Ignore));
        Suggest();
        Status = "File available. Verify column roles, confirm the mapping, then add it to the import. Add query/corpus/qrel files separately.";
    }
    private void Suggest()
    {
        var mapping = MappingRules.Suggest(Kind, Columns.Select(c => c.Column).ToArray());
        foreach (var column in Columns) column.Role = mapping.Columns.GetValueOrDefault(column.Column);
        MappingConfirmed = false; OnPropertyChanged(nameof(MappingConfirmed));
    }
    private Task AddInputAsync()
    {
        if (preparedPath is null || !MappingConfirmed) throw new InvalidOperationException("Download/open a file and explicitly confirm its column mapping.");
        var mapping = new TableMapping { Path = preparedPath, Kind = Kind, Confirmed = true,
            Columns = Columns.Where(c => c.Role != ColumnRole.Ignore).ToDictionary(c => c.Column, c => c.Role) };
        if (!CaptionPairs)
        {
            var validation = MappingRules.Validate(Kind, mapping.Columns);
            if (!validation.IsValid) throw new InvalidDataException(validation.ToString());
        }
        Inputs.Add(new(preparedPath, mapping, preparedHub, preparedFile, preparedConfiguration?.Name, preparedConfiguration?.Split, CaptionPairs, ImageQueries));
        Status = $"{Inputs.Count} input(s) queued. Cross-repository corpus/query/qrel pins are retained separately.";
        return System.Threading.Tasks.Task.CompletedTask;
    }
    private async Task BuildAsync()
    {
        var root = Path.Combine(AppPaths.Root, "datasets", Inputs.All(i => i.Hub is not null) ? "public" : "local", "normalized", Guid.NewGuid().ToString("N"));
        var path = await DatasetImport.BuildAsync(DatasetName, Task, Inputs.ToArray(), RowsPerFile, root, Token);
        settings.Update(s => SettingsStore.AddRecent(s.RecentDatasets, path));
        AddLocalCard(path);
        Status = "Normalized dataset saved with pinned inputs, subset/mapping fingerprint and image hashes. Preview it, then add to evaluation.";
    }
    private void AddLocalCard(string path)
    {
        if (!path.EndsWith(".smds.json", StringComparison.OrdinalIgnoreCase))
        {
            var manifest = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty cached EvalKit manifest.");
            var name = manifest["description"]?["name"]?.GetValue<string>() ?? throw new InvalidDataException("Cached EvalKit dataset name is missing.");
            var version = manifest["description"]?["version"]?.GetValue<string>() ?? "";
            AddCard(new(name, DatasetSource.MicrosoftInternal, version, "See manifest", "See manifest", "Preview for count",
                "See manifest", "Local internal cache", "Policy checked before use", "Cached", path));
            return;
        }
        var m = DatasetManifest.Load(path);
        AddCard(new(m.Name, m.Provenance?.Source ?? DatasetSource.Local, m.Version, m.Task.ToString(),
            m.Tables.Any(t => t.Columns.ContainsValue(ColumnRole.ImagePath)) ? "Images / mixed" : "Text", "Preview for count",
            m.Tables.Any(t => t.Kind == TableKind.Judgments) ? "Judgment table present" : "Unknown",
            "Local; integrity checked on load", m.Provenance?.RequiresApproval == true ? "Pending - blocked" : "See source terms", "Cached", path));
    }
    private Task RefreshLocalAsync()
    {
        var paths = settings.Current.RecentDatasets.Where(File.Exists).Where(p =>
            p.EndsWith(".smds.json", StringComparison.OrdinalIgnoreCase) || File.Exists(p + ".smeval-handling.json")).ToList();
        var samples = Path.Combine(AppContext.BaseDirectory, "samples");
        if (Directory.Exists(samples)) paths.AddRange(Directory.EnumerateFiles(samples, "*.smds.json", SearchOption.AllDirectories));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) AddLocalCard(path);
        Status = "Recent and bundled datasets listed. Existing Dataset Studio imports remain available.";
        return System.Threading.Tasks.Task.CompletedTask;
    }
    private async Task ListInternalAsync()
    {
        if (!dialogs.Confirm($"List dataset metadata from {Container} with your authorized Microsoft sign-in? No payload is downloaded.")) return;
        foreach (var entry in await catalog.ListAsync(Container, Token))
            AddCard(new(entry.Name, DatasetSource.MicrosoftInternal, entry.Version, "Inspect matching manifest", "Unknown", "Unknown",
                "Unknown until matching payload", "Catalog access authorized; payload access checked separately", entry.Approval, "Not downloaded", Internal: entry));
        Status = "Registered identities/versions came from EvalKit. ADO inventory labels do not establish download paths or approvals.";
    }
    private async Task FetchMatchingAsync()
    {
        var card = Selected ?? throw new InvalidOperationException("Select an exact EvalKit catalog entry.");
        var entry = card.Internal ?? throw new InvalidOperationException("List the authorized catalog and select a registered version, not an inventory family.");
        if (string.IsNullOrWhiteSpace(PayloadHash)) throw new InvalidOperationException("Provide the payload hash recorded in the matching EvalKit run. Alternatively use Compare two models > Get matching dataset.");
        if (!dialogs.Confirm("Fetch this one exact dataset using the existing EvalKit CLI? Governed data without approval evidence remains blocked.")) return;
        var path = await cli.FetchDatasetAsync(new(entry.Name, entry.Version, PayloadHash), entry.Container, new Progress<string>(s => Status = s), Token);
        settings.Update(s => SettingsStore.AddRecent(s.RecentDatasets, path));
        AddCard(card with { Manifest = path, Cache = "Cached; exact run identity matched" });
        Status = "Exact dataset retrieved through EvalKit; no substitute version was attached.";
    }
    private async Task FetchNewAsync()
    {
        var card = Selected ?? throw new InvalidOperationException("Select an exact EvalKit catalog entry.");
        var entry = card.Internal ?? throw new InvalidOperationException("List the authorized catalog and select a registered version, not an inventory family.");
        if (!dialogs.Confirm($"Fetch {entry.Name} v{entry.Version} from {entry.Container} for a NEW evaluation? " +
            "The CLI verifies the payload and its identity is retained. This does not substitute a dataset for an existing scored run. Approval restrictions still apply.")) return;
        var path = await cli.FetchCatalogDatasetAsync(entry, new Progress<string>(s => Status = s), Token);
        settings.Update(s => SettingsStore.AddRecent(s.RecentDatasets, path));
        AddCard(card with { Manifest = path, Cache = "Cached; selected version verified" });
        Status = "Dataset cached for a new evaluation. Select Add to evaluation or Use in two-model comparison; saved-run identity checks remain enforced.";
    }
    private string RequireManifest() => Selected?.Manifest ?? throw new InvalidOperationException("Select a cached normalized or exact EvalKit dataset first.");
    private async Task PreviewAsync()
    {
        var dataset = await System.Threading.Tasks.Task.Run(() => DatasetLoader.Load(RequireManifest()), Token);
        DatasetPolicy.Ensure(dataset, DatasetOperation.Preview);
        var table = new DataTable(); table.Columns.Add("Id"); table.Columns.Add("Text"); table.Columns.Add("Image");
        foreach (var q in dataset.Queries.Take(10)) table.Rows.Add(q.Id, q.Text, q.ImagePath is null ? "none" : "local image");
        foreach (var e in dataset.Examples.Take(10)) table.Rows.Add(e.Id, e.Text, e.ImagePath is null ? "none" : "local image");
        Preview = table.DefaultView;
        Details = $"{dataset.Name} v{dataset.Version}\n{dataset.RecordCount} examples; {dataset.Documents.Count} candidates; {dataset.Judgments.Count} judgments.\n" +
            $"{dataset.Description}\n" + string.Join("\n", dataset.ValidationIssues.Select(i => i.Message));
        var compatible = workbench.Models.Where(m => EvaluationEngine.CheckCompatibility([new(m.Model)], dataset, dataset.Task).CanRun).Select(m => m.Name);
        Status = "Compatible available models: " + string.Join(", ", compatible.DefaultIfEmpty("none; use imported predictions or register a compatible runtime"));
    }
    public void Dispose() => operation?.Cancel();
}
