using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Security;

namespace SemanticModelEvaluator.App;

public sealed class MappingColumn
{
    public required string Name { get; init; }
    public required string Preview { get; init; }
    public required IReadOnlyList<ColumnRole> Roles { get; init; }
    public ColumnRole Role { get; set; }
}

public partial class MappingWindow : Window
{
    private TabularData? table;
    private readonly ObservableCollection<TableMapping> mappings = [];
    public MappingWindow()
    {
        InitializeComponent();
        TaskChoice.ItemsSource = Enum.GetValues<EvalTask>(); TaskChoice.SelectedIndex = 0;
        KindChoice.ItemsSource = Enum.GetValues<TableKind>(); KindChoice.SelectedIndex = 0;
        Tables.ItemsSource = mappings;
    }
    private TableKind Kind => (TableKind)KindChoice.SelectedItem;
    private void KindChanged(object sender, SelectionChangedEventArgs e) { if (table is not null) Preview(); }
    private void Preview()
    {
        if (table is null) return;
        var suggested = MappingRules.Suggest(Kind, table.Columns);
        Columns.ItemsSource = table.Columns.Select((name, i) => new MappingColumn
        {
            Name = name, Role = suggested.Columns[name], Roles = MappingRules.AllowedRoles(Kind),
            Preview = string.Join(" | ", table.Rows.Take(3).Select(r => r[i]))
        }).ToList();
        Message.Text = $"{table.Rows.Count} rows. " + string.Join("; ", suggested.Ambiguities.Concat(suggested.MissingRequired));
    }
    private async void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Dataset tables|*.csv;*.tsv;*.json;*.jsonl;*.ndjson" };
        if (dialog.ShowDialog(this) != true) return;
        IsEnabled = false;
        try { table = await Task.Run(() => TabularReader.Read(dialog.FileName)); Preview(); }
        catch (Exception ex) { Message.Text = Redactor.Redact(ex.Message); }
        finally { IsEnabled = true; }
    }
    private Dictionary<string, ColumnRole> Mapping() => Columns.ItemsSource is IEnumerable<MappingColumn> columns
        ? columns.ToDictionary(c => c.Name, c => c.Role) : throw new InvalidOperationException("Browse a source table first.");
    private void AddTable(object sender, RoutedEventArgs e)
    {
        try
        {
            if (table is null) throw new InvalidOperationException("Browse a source table first.");
            var columns = Mapping(); var report = MappingRules.Validate(Kind, columns);
            if (!report.IsValid) throw new InvalidDataException(report.ToString());
            if (mappings.Any(m => m.Kind == Kind)) throw new InvalidDataException("Remove the existing mapping of this table kind before replacing it.");
            mappings.Add(new TableMapping { Kind = Kind, Path = table.SourcePath, Columns = columns, Confirmed = true });
            Message.Text = "Confirmed " + Kind + " mapping. Add other required tables, then save the manifest.";
        }
        catch (Exception ex) { Message.Text = Redactor.Redact(ex.Message); }
    }
    private void RemoveTable(object sender, RoutedEventArgs e) { if (Tables.SelectedItem is TableMapping mapping) mappings.Remove(mapping); }
    private async void SaveManifest(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(DatasetName.Text) || string.IsNullOrWhiteSpace(Version.Text))
                throw new InvalidDataException("Name and version are required.");
            var manifest = new DatasetManifest { Name = DatasetName.Text, Version = Version.Text, Task = (EvalTask)TaskChoice.SelectedItem,
                DataClassification = Classification.Text, PositiveLabel = string.IsNullOrWhiteSpace(Positive.Text) ? null : Positive.Text,
                Synthetic = Synthetic.IsChecked == true, Tables = [.. mappings] };
            var loaded = await Task.Run(() =>
            {
                var tables = manifest.Tables.Select(m => (m, TabularReader.Read(m.Path))).ToList();
                return DatasetBuilder.Build(manifest, tables, Environment.CurrentDirectory, null, "");
            });
            if (loaded.ValidationIssues.Any(i => i.Severity == ValidationSeverity.Error))
                throw new InvalidDataException(string.Join("\n", loaded.ValidationIssues.Select(i => i.Message)));
            var dialog = new SaveFileDialog { Filter = "Dataset manifest|*.smds.json", DefaultExt = ".smds.json" };
            if (dialog.ShowDialog(this) != true) return;
            await Task.Run(() => manifest.Save(dialog.FileName));
            Message.Text = "Saved: " + dialog.FileName;
        }
        catch (Exception ex) { Message.Text = Redactor.Redact(ex.Message); }
        finally { IsEnabled = true; }
    }
    private void SaveProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = "Mapping profile|*.json", DefaultExt = ".json" };
            if (dialog.ShowDialog(this) != true) return;
            var profile = new MappingProfile { Name = Path.GetFileNameWithoutExtension(dialog.FileName), Kind = Kind, Columns = Mapping() };
            Core.AtomicFile.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profile, DatasetManifest.JsonOptions));
            Message.Text = "Profile saved.";
        }
        catch (Exception ex) { Message.Text = Redactor.Redact(ex.Message); }
    }
    private void LoadProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (table is null) throw new InvalidOperationException("Browse a table first.");
            var dialog = new OpenFileDialog { Filter = "Mapping profile|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            var profile = JsonSerializer.Deserialize<MappingProfile>(File.ReadAllText(dialog.FileName), DatasetManifest.JsonOptions)
                ?? throw new InvalidDataException("Empty profile.");
            if (profile.Kind != Kind) throw new InvalidDataException("The profile is for a different table kind.");
            if (Columns.ItemsSource is not List<MappingColumn> columns) return;
            foreach (var column in columns) column.Role = profile.Columns.GetValueOrDefault(column.Name, ColumnRole.Ignore);
            Columns.Items.Refresh(); Message.Text = "Profile loaded. Review and confirm the mapping before use.";
        }
        catch (Exception ex) { Message.Text = Redactor.Redact(ex.Message); }
    }
}
