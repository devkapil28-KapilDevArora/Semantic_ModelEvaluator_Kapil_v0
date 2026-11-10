using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SemanticModelEvaluator.App.Services;
using SemanticModelEvaluator.App.ViewModels;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.EvalKit;
using SemanticModelEvaluator.Providers.Datasets;
using SemanticModelEvaluator.Providers.Ffa;

namespace SemanticModelEvaluator.App;

public partial class App : Application
{
    private ServiceProvider? services;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Length == 2 && e.Args[0] == "--data-root") AppPaths.SetRoot(Path.GetFullPath(e.Args[1]));
            var settings = new SettingsStore(AppPaths.SettingsFile);
            var network = new NetworkPolicy { OfflineMode = settings.Current.OfflineMode };
            var collection = new ServiceCollection()
                .AddSingleton(settings).AddSingleton(network).AddSingleton<AppLog>()
                .AddSingleton<ISecretStore, WindowsCredentialStore>()
                .AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                .AddSingleton<IModelProvider, DeterministicProvider>()
                .AddSingleton<IModelProvider, LocalOnnxProvider>()
                .AddSingleton<IModelProvider, ImportedPredictionsProvider>()
                .AddSingleton<IModelProvider, HuggingFaceProvider>()
                .AddSingleton<IModelProvider, FfaProvider>()
                .AddSingleton<IModelProvider>(sp => new FoundryProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<NetworkPolicy>(), sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<SettingsStore>()))
                .AddSingleton<IModelProvider>(sp => new PsapiProvider(sp.GetRequiredService<EvalKitCloudService>().ListCachedBundles, () => PsapiBackendStatus.Probe(sp.GetRequiredService<EvalKitCli>())))
                .AddSingleton(sp => new EvalKitCloudService(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<NetworkPolicy>()))
                .AddSingleton<EvalKitCli>()
                .AddSingleton<EvalKitDatasetCatalog>()
                .AddSingleton(sp => new HuggingFaceDatasets(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<NetworkPolicy>()))
                .AddSingleton<DatasetHubViewModel>()
                .AddSingleton<ProviderRegistry>().AddSingleton(new MetricRegistry())
                .AddSingleton(new EmbeddingCache(AppPaths.EmbeddingCache))
                .AddSingleton<IRunStore>(new FileRunStore(AppPaths.Runs))
                .AddSingleton<EvaluationEngine>()
                .AddSingleton<IUserDialogs, DesktopDialogs>()
                .AddSingleton<WorkbenchViewModel>().AddSingleton<PairComparisonViewModel>().AddSingleton<MainWindow>();
            services = collection.BuildServiceProvider();
            MainWindow = services.GetRequiredService<MainWindow>();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Redactor.Redact(ex.Message), AppPaths.AppName + " - startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { services?.Dispose(); base.OnExit(e); }
}
