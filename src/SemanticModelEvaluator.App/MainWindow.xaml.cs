using System.Windows;
using System.Windows.Media;
using SemanticModelEvaluator.App.ViewModels;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Providers;

namespace SemanticModelEvaluator.App;

public partial class MainWindow : Window
{
    private readonly WorkbenchViewModel model;
    private readonly SettingsStore settings;
    private readonly ProviderRegistry providers;
    private readonly ISecretStore secrets;
    public MainWindow(WorkbenchViewModel model, PairComparisonViewModel pair, SettingsStore settings, ProviderRegistry providers, ISecretStore secrets)
    {
        InitializeComponent();
        this.model = model; this.settings = settings; this.providers = providers; this.secrets = secrets; DataContext = model; PairTab.DataContext = pair;
        ApplyTheme(settings.Current.Theme == "Dark");
        model.ThemeRequested += () =>
        {
            bool dark = settings.Current.Theme != "Dark";
            settings.Update(s => s.Theme = dark ? "Dark" : "Light");
            ApplyTheme(dark);
        };
        model.MappingRequested += () => new MappingWindow { Owner = this }.ShowDialog();
        Loaded += async (_, _) => await model.InitializeAsync();
        Closing += (_, e) =>
        {
            if (model.Busy || pair.Busy) { MessageBox.Show(this, "Cancel the current operation and wait for it to finish before closing."); e.Cancel = true; return; }
            if (model.Dirty && MessageBox.Show(this, "Close without saving the current workspace?", Core.AppPaths.AppName, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => model.Dispose();
    }
    private void ProviderSettings(object sender, RoutedEventArgs e) => new ProviderSettingsWindow(providers, settings, secrets) { Owner = this }.ShowDialog();
    private static void ApplyTheme(bool dark)
    {
        var colors = dark
            ? new[] { "#151C29", "#202B3C", "#EDF2FA", "#B6C5DA", "#49566B" }
            : new[] { "#F3F5F9", "#FFFFFF", "#172B4D", "#52627A", "#C7D1E0" };
        var keys = new[] { "BackgroundBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "BorderBrush" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
