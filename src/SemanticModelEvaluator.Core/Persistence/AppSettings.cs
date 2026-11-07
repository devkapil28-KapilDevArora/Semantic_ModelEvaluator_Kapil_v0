using System.Text.Json;

namespace SemanticModelEvaluator.Core.Persistence;

public sealed class FfaWorkerBinding
{
    public string Name { get; set; } = "";
    public string WorkerExecutable { get; set; } = "";
    public string ModelDirectory { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public int? Dimensions { get; set; }
    public string? ContentHash { get; set; }
}

public sealed class ImportedPredictionsEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Family { get; set; } = "Imported";
    public string Task { get; set; } = "Retrieval";
    public string Notes { get; set; } = "";
}

/// <summary>
/// Configuration reference for an Azure AI Foundry / Azure OpenAI embedding deployment. Contains no secrets:
/// Entra ID is the default; an API key, if chosen, lives in Windows Credential Manager under <see cref="KeyName"/>.
/// </summary>
public sealed class FoundryDeployment
{
    public string Name { get; set; } = "";
    /// <summary>https://&lt;resource&gt;.openai.azure.com or https://&lt;resource&gt;.services.ai.azure.com</summary>
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    /// <summary>"OpenAIv1" (&lt;endpoint&gt;/openai/v1/embeddings, the pattern used in ASG-SearchPlatform DataPipelines azure_v1.py)
    /// or "AzureOpenAIDeployment" (/openai/deployments/{d}/embeddings?api-version=...).</summary>
    public string ApiStyle { get; set; } = "OpenAIv1";
    /// <summary>Only used by the AzureOpenAIDeployment style.</summary>
    public string ApiVersion { get; set; } = "2024-10-21";
    /// <summary>User-declared model name/version behind the deployment (the service may not report it).</summary>
    public string ModelVersion { get; set; } = "";
    public int? Dimensions { get; set; }
    /// <summary>"EntraId" (DefaultAzureCredential) or "ApiKey" (Credential Manager).</summary>
    public string Auth { get; set; } = "EntraId";
    public string? TenantId { get; set; }
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";
    /// <summary>Optional prices per 1K input tokens, only used when explicitly configured.</summary>
    public double? PricePer1KInputTokens { get; set; }
    public string KeyName => "foundry:" + Name;
}

/// <summary>EvalKit data locations. Defaults mirror EvalKit.Viewer; environment variables of the same names override.</summary>
public sealed class EvalKitSettings
{
    public string ResultsAccount { get; set; } = "scaievalkitdatasets";
    public string NonSensitiveResultsContainer { get; set; } = "eval-nonsensitive-results";
    public string SensitiveResultsContainer { get; set; } = "eval-sensitive-results";
    public string DatasetAccount { get; set; } = "scaievalkitdatasets";
    public string? TenantId { get; set; }
    /// <summary>EvalKit.CLI.dll or evalkit.exe; empty = discover under an ASG-SearchPlatform checkout.</summary>
    public string? CliPath { get; set; }
    public string? RepositoryRoot { get; set; }
    public string? ResultCacheDirectory { get; set; }
    public string? DatasetCacheDirectory { get; set; }
    public List<string> LocalRunFolders { get; set; } = [];
}

public sealed class AppSettings
{
    public List<FoundryDeployment> FoundryDeployments { get; set; } = [];
    public EvalKitSettings EvalKit { get; set; } = new();
    public string Theme { get; set; } = "Light";
    public bool OfflineMode { get; set; }
    public bool PreferQuantizedOnnx { get; set; }
    public int InferenceThreads { get; set; }
    public List<string> LocalModelFolders { get; set; } = [];
    public List<FfaWorkerBinding> FfaBindings { get; set; } = [];
    public List<ImportedPredictionsEntry> ImportedPredictions { get; set; } = [];
    public List<string> RecentWorkspaces { get; set; } = [];
    public List<string> RecentDatasets { get; set; } = [];
}

public sealed class SettingsStore(string path)
{
    private readonly object gate = new();
    private AppSettings? current;

    public string Path { get; } = path;

    public AppSettings Current
    {
        get
        {
            lock (gate)
            {
                if (current is not null) return current;
                try
                {
                    current = File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), WorkspaceSerializer.Options) ?? new() : new();
                }
                catch (JsonException ex) { throw new InvalidDataException($"Settings file '{Path}' is invalid. Restore or rename it before retrying.", ex); }
                return current;
            }
        }
    }

    public void Save()
    {
        lock (gate) AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(Current, WorkspaceSerializer.Options));
    }

    public void Update(Action<AppSettings> change)
    {
        lock (gate) { change(Current); Save(); }
    }

    public static void AddRecent(List<string> list, string item, int max = 10)
    {
        list.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, item);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }
}
