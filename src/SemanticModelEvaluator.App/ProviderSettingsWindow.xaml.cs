using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.Ffa;

namespace SemanticModelEvaluator.App;

public partial class ProviderSettingsWindow : Window
{
    private readonly HuggingFaceProvider hub;
    private readonly FfaProvider ffa;
    private readonly SettingsStore settings;
    private readonly ISecretStore secrets;
    public ProviderSettingsWindow(ProviderRegistry providers, SettingsStore settings, ISecretStore secrets)
    {
        InitializeComponent();
        hub = (HuggingFaceProvider)providers.Get("huggingface");
        ffa = (FfaProvider)providers.Get("ffa");
        this.settings = settings; this.secrets = secrets;
        RefreshDeployments();
        var ek = settings.Current.EvalKit;
        EAccount.Text = ek.ResultsAccount; ENonSensitive.Text = ek.NonSensitiveResultsContainer; ESensitive.Text = ek.SensitiveResultsContainer;
        EDatasetAccount.Text = ek.DatasetAccount; ETenant.Text = ek.TenantId ?? ""; ECli.Text = ek.CliPath ?? ""; ERepo.Text = ek.RepositoryRoot ?? "";
        EResultCache.Text = ek.ResultCacheDirectory ?? ""; EDatasetCache.Text = ek.DatasetCacheDirectory ?? "";
    }

    private void RefreshDeployments() =>
        Deployments.ItemsSource = settings.Current.FoundryDeployments.Select(d => $"{d.Name} · {d.Deployment} · {d.Auth}").ToList();

    private void DeploymentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Deployments.SelectedIndex < 0 || Deployments.SelectedIndex >= settings.Current.FoundryDeployments.Count) return;
        var d = settings.Current.FoundryDeployments[Deployments.SelectedIndex];
        FName.Text = d.Name; FEndpoint.Text = d.Endpoint; FDeployment.Text = d.Deployment; FApiVersion.Text = d.ApiVersion;
        FApiStyle.SelectedIndex = d.ApiStyle == "AzureOpenAIDeployment" ? 1 : 0; FAuth.SelectedIndex = d.Auth == "ApiKey" ? 1 : 0;
        FModelVersion.Text = d.ModelVersion; FDimensions.Text = d.Dimensions?.ToString(CultureInfo.InvariantCulture) ?? "";
        FTenant.Text = d.TenantId ?? ""; FPrice.Text = d.PricePer1KInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private void SaveDeployment(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = FName.Text.Trim();
            if (name.Length == 0) throw new InvalidOperationException("Enter a display name.");
            if (!Uri.TryCreate(FEndpoint.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("Endpoint must be an https URL without a query string or credentials.");
            if (FDeployment.Text.Trim().Length == 0) throw new InvalidOperationException("Enter the deployment name.");
            int? dims = null;
            if (FDimensions.Text.Trim().Length > 0)
                dims = int.TryParse(FDimensions.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv) && dv > 0 ? dv : throw new InvalidOperationException("Dimensions must be a positive integer.");
            double? price = null;
            if (FPrice.Text.Trim().Length > 0)
                price = double.TryParse(FPrice.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv) && pv >= 0 && double.IsFinite(pv) ? pv : throw new InvalidOperationException("Price must be a non-negative number.");
            var d = new FoundryDeployment
            {
                Name = name, Endpoint = uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), Deployment = FDeployment.Text.Trim(),
                ApiStyle = ((ComboBoxItem)FApiStyle.SelectedItem).Content.ToString()!, ApiVersion = FApiVersion.Text.Trim(),
                ModelVersion = FModelVersion.Text.Trim(), Dimensions = dims, Auth = ((ComboBoxItem)FAuth.SelectedItem).Content.ToString()!,
                TenantId = string.IsNullOrWhiteSpace(FTenant.Text) ? null : FTenant.Text.Trim(), PricePer1KInputTokens = price
            };
            if (d.Auth == "ApiKey")
            {
                if (FKey.Password.Length > 0) secrets.Write(d.KeyName, FKey.Password.Trim());
                else if (secrets.Read(d.KeyName) is null) throw new InvalidOperationException("ApiKey authentication needs a key; none is stored for this name.");
            }
            settings.Update(s => { s.FoundryDeployments.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); s.FoundryDeployments.Add(d); });
            RefreshDeployments();
            Status.Text = $"Deployment '{name}' saved{(d.Auth == "ApiKey" ? "; key held in Windows Credential Manager" : "")}. Re-select the AI Foundry provider to refresh.";
        }
        catch (Exception ex) { Status.Text = Redactor.Redact(ex.Message); }
        finally { FKey.Clear(); }
    }

    private void RemoveDeployment(object sender, RoutedEventArgs e)
    {
        if (Deployments.SelectedIndex < 0 || Deployments.SelectedIndex >= settings.Current.FoundryDeployments.Count) { Status.Text = "Select a deployment first."; return; }
        var d = settings.Current.FoundryDeployments[Deployments.SelectedIndex];
        if (MessageBox.Show(this, $"Remove deployment '{d.Name}' and any stored key?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            try { secrets.Delete(d.KeyName); } catch (Exception) { /* no stored key */ }
            settings.Update(s => s.FoundryDeployments.RemoveAll(x => x.Name == d.Name));
            RefreshDeployments(); Status.Text = "Deployment removed.";
        }
        catch (Exception ex) { Status.Text = Redactor.Redact(ex.Message); }
    }

    private void SaveEvalKit(object sender, RoutedEventArgs e)
    {
        static string? Opt(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        try
        {
            foreach (var v in new[] { EAccount.Text, ENonSensitive.Text, ESensitive.Text, EDatasetAccount.Text })
                if (string.IsNullOrWhiteSpace(v) || v.Contains("://") || v.Contains(';') || v.Contains('='))
                    throw new InvalidOperationException("Account and container fields take plain names only (no URLs, keys or connection strings).");
            settings.Update(s =>
            {
                var ek = s.EvalKit;
                ek.ResultsAccount = EAccount.Text.Trim(); ek.NonSensitiveResultsContainer = ENonSensitive.Text.Trim(); ek.SensitiveResultsContainer = ESensitive.Text.Trim();
                ek.DatasetAccount = EDatasetAccount.Text.Trim(); ek.TenantId = Opt(ETenant.Text); ek.CliPath = Opt(ECli.Text); ek.RepositoryRoot = Opt(ERepo.Text);
                ek.ResultCacheDirectory = Opt(EResultCache.Text); ek.DatasetCacheDirectory = Opt(EDatasetCache.Text);
            });
            Status.Text = "EvalKit settings saved.";
        }
        catch (Exception ex) { Status.Text = Redactor.Redact(ex.Message); }
    }
    private void StoreToken(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Token.Password)) throw new InvalidOperationException("Enter a token first.");
            hub.SetToken(Token.Password); Status.Text = "Token stored in Windows Credential Manager.";
        }
        catch (Exception ex) { Status.Text = Redactor.Redact(ex.Message); }
        finally { Token.Clear(); }
    }
    private void DeleteToken(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete the stored Hugging Face token?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { hub.SetToken(null); Status.Text = "Token deleted."; }
        catch (Exception ex) { Status.Text = Redactor.Redact(ex.Message); }
    }
    private void BrowseWorker(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Worker executable|*.exe" };
        if (dialog.ShowDialog(this) == true) Worker.Text = dialog.FileName;
    }
    private void BrowseModel(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) ModelFolder.Text = dialog.FolderName;
    }
    private async void RegisterWorker(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Trust this worker executable to process your datasets locally?\n" + Worker.Text, Title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var binding = new FfaWorkerBinding { Name = BindingName.Text, WorkerExecutable = Worker.Text,
            ModelDirectory = ModelFolder.Text, ProviderId = ProviderArgument.Text };
        IsEnabled = false;
        try { await Task.Run(() => ffa.AddBinding(binding)); Status.Text = "Binding registered. Refresh local models in Model Hub."; }
        catch (Exception ex) { Status.Text = Redactor.Redact(ex.Message); }
        finally { IsEnabled = true; }
    }
}
