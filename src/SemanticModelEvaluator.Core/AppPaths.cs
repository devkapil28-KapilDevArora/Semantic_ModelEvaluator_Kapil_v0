using System.Text;

namespace SemanticModelEvaluator.Core;

public static class AtomicFile
{
    /// <summary>Writes via a temp file and atomic replace so a crash never leaves a half-written workspace.</summary>
    public static void WriteAllText(string path, string content)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        try
        {
            if (File.Exists(full)) File.Replace(temp, full, null, ignoreMetadataErrors: true);
            else File.Move(temp, full);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        await Task.Run(() => WriteAllText(path, content), ct).ConfigureAwait(false);
    }
}

public static class AppPaths
{
    public const string AppName = "Semantic_ModelEvaluator_Kapil_V0";
    public const string WorkspaceExtension = ".smeval";
    public const string DatasetManifestExtension = ".smds.json";

    private static string? root;

    /// <summary>Override for tests.</summary>
    public static void SetRoot(string? path) => root = path;

    public static string Root => root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static string Models => Ensure(Path.Combine(Root, "models"));
    public static string EmbeddingCache => Ensure(Path.Combine(Root, "cache", "embeddings"));
    public static string Runs => Ensure(Path.Combine(Root, "runs"));
    public static string Logs => Ensure(Path.Combine(Root, "logs"));
    public static string Profiles => Ensure(Path.Combine(Root, "mapping-profiles"));
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");

    private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}
