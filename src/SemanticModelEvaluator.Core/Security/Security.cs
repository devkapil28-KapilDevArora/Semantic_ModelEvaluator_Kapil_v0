using System.Text.RegularExpressions;

namespace SemanticModelEvaluator.Core.Security;

/// <summary>Removes credentials and user-identifying paths from text destined for logs, diagnostics and error messages.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"\bhf_[A-Za-z0-9]{8,}\b")] private static partial Regex HfToken();
    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-\._~\+/=]{8,}")] private static partial Regex AuthHeader();
    [GeneratedRegex(@"(?i)\b(authorization|api[-_]?key|apikey|subscription[-_]?key|ocp-apim-subscription-key|x-api-key|instrumentation[-_]?key|access[-_]?key|account[-_]?key|private[-_]?key|key|access[-_]?token|refresh[-_]?token|id[-_]?token|token|password|passwd|pwd|secret|client[-_]?secret|credential|sig|sas)\b(\s*[:=]\s*|"":\s*"")([^\s&;,""']+)")] private static partial Regex KeyValue();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}\b")] private static partial Regex Jwt();
    [GeneratedRegex(@"(?i)\b(gh[pousr]_[A-Za-z0-9]{20,})\b")] private static partial Regex GitHubToken();
    [GeneratedRegex(@"(?i)\b(AccountKey|SharedAccessSignature|SharedAccessKey|SharedAccessKeyName|Password|ClientSecret|InstrumentationKey|ApiKey|Key)=([^;\s""']+)")] private static partial Regex ConnectionStringSecret();
    [GeneratedRegex(@"(?i)([?&](sig|se|sv|sp|skoid|sktid|skt|ske|sks|skv|st|spr|srt|ss|sr|code|key|api-key|subscription-key)=)[^&\s""'<>]+")] private static partial Regex SasQuery();
    [GeneratedRegex(@"(?i)(api[-_]?key|apikey|subscription[-_]?key|secret|password|passwd|pwd|token$|credential|connection[-_]?string|conn[-_]?str|(^|[-_])sas($|[-_])|signature|account[-_]?key|private[-_]?key|^key$|[-_]key$)")] private static partial Regex SensitiveName();

    /// <summary>True when a parameter or setting name indicates its value is a credential; such values must be suppressed entirely.</summary>
    public static bool IsSecretName(string? name) => !string.IsNullOrWhiteSpace(name) && SensitiveName().IsMatch(name.Trim());

    /// <summary>Formats a named value for display, suppressing it entirely when the name is credential-like or the value looks like a connection string.</summary>
    public static string RedactValue(string? name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        if (IsSecretName(name)) return "***";
        if (value.Contains(';') && value.Contains('=') && (value.Contains("AccountName", StringComparison.OrdinalIgnoreCase) || value.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Key", StringComparison.OrdinalIgnoreCase) || value.Contains("Password", StringComparison.OrdinalIgnoreCase))) return "*** (connection string suppressed)";
        return Redact(value);
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = ConnectionStringSecret().Replace(text, m => m.Groups[1].Value + "=***");
        s = SasQuery().Replace(s, m => m.Groups[1].Value + "***");
        s = HfToken().Replace(s, "hf_***");
        s = GitHubToken().Replace(s, "gh*_***");
        s = Jwt().Replace(s, "***jwt***");
        s = AuthHeader().Replace(s, m => m.Groups[1].Value + " ***");
        s = KeyValue().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "***");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return s;
    }
}

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message);

/// <summary>
/// Application log. Messages are always redacted. Callers must log identifiers and counts — never prompts, queries or document text.
/// </summary>
public sealed class AppLog
{
    private readonly object gate = new();
    private readonly List<LogEntry> entries = [];
    private readonly string? file;

    public AppLog(string? directory = null)
    {
        if (directory is not null) file = Path.Combine(directory, $"smeval-{DateTime.Now:yyyyMMdd}.log");
    }

    public event Action<LogEntry>? Logged;

    public IReadOnlyList<LogEntry> Entries { get { lock (gate) return [.. entries]; } }

    public void Write(LogLevel level, string category, string message)
    {
        var e = new LogEntry(DateTimeOffset.Now, level, category, Redactor.Redact(message));
        lock (gate)
        {
            entries.Add(e);
            if (entries.Count > 5000) entries.RemoveRange(0, 1000);
            if (file is not null)
                try { File.AppendAllText(file, $"{e.Time:O}\t{e.Level}\t{e.Category}\t{e.Message}{Environment.NewLine}"); } catch (IOException) { }
        }
        Logged?.Invoke(e);
    }

    public void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Write(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Write(LogLevel.Error, category, message);
}

/// <summary>Central switch for outbound network access. When offline, network providers must refuse remote calls.</summary>
public sealed class NetworkPolicy
{
    public bool OfflineMode { get; set; }

    public event Action<string, Uri>? NetworkAccess;

    public void EnsureAllowed(string purpose, Uri uri)
    {
        if (OfflineMode) throw new InvalidOperationException($"Offline mode is on; '{purpose}' needs network access to {uri.Host}.");
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Only HTTPS connections are permitted.");
        NetworkAccess?.Invoke(purpose, uri);
    }
}
