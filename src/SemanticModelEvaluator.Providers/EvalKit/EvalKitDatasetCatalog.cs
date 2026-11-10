using System.Diagnostics;
using System.Text.RegularExpressions;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;

namespace SemanticModelEvaluator.Providers.EvalKit;

public sealed record EvalKitDatasetEntry(string Name, string Version, string Summary, EvalKitDatasetContainer Container)
{
    public string Approval => "Not established by catalog listing; privacy/CELA/use/retention evidence required for governed evaluation.";
    public override string ToString() => $"{Name} v{Version} ({Container.State})";
}

/// <summary>Uses the authoritative CLI catalog, not ADO inventory text or guessed blob paths.</summary>
public sealed class EvalKitDatasetCatalog(EvalKitCli cli, SettingsStore settings, NetworkPolicy network)
{
    public static IReadOnlyList<string> Arguments(string account, EvalKitDatasetContainer container) =>
        ["list", "--all-versions", "--storage-account", account, "--container", container.Name, .. container.Working ? new[] { "--staging" } : []];

    public async Task<IReadOnlyList<EvalKitDatasetEntry>> ListAsync(EvalKitDatasetContainer container, CancellationToken ct)
    {
        network.EnsureAllowed("Authorized EvalKit dataset catalog", new Uri($"https://{settings.Current.EvalKit.DatasetAccount}.blob.core.windows.net"));
        var executable = cli.Locate() ?? throw new FileNotFoundException("Build the authorized EvalKit CLI or configure its path in Provider settings.");
        var start = new ProcessStartInfo
        {
            FileName = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : executable,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        if (start.FileName == "dotnet") start.ArgumentList.Add(executable);
        foreach (var argument in Arguments(settings.Current.EvalKit.DatasetAccount, container)) start.ArgumentList.Add(argument);
        foreach (var (key, value) in EvalKitCli.FetchEnvironment(container)) start.Environment[key] = value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("EvalKit catalog process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var error = await stderr.ConfigureAwait(false);
            if (error.Contains("403") || error.Contains("401") || error.Contains("denied", StringComparison.OrdinalIgnoreCase) || error.Contains("az login", StringComparison.OrdinalIgnoreCase))
                throw new AccessDeniedException("EvalKit catalog access denied. Use the authorized Microsoft/CoreIdentity sign-in. No other identity or container was tried.");
            throw new InvalidOperationException($"EvalKit catalog exited {process.ExitCode}. Run the configured CLI's 'list' command to inspect its local diagnostics.");
        }
        return Parse(await stdout.ConfigureAwait(false), container);
    }

    public static IReadOnlyList<EvalKitDatasetEntry> Parse(string output, EvalKitDatasetContainer container)
    {
        output = Regex.Replace(output, @"\x1B\[[0-9;]*m", "");
        if (output.Contains("[stale]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EvalKit returned a stale offline catalog. It cannot establish current authorization; reconnect and retry.");
        var entries = new List<EvalKitDatasetEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Trim() == "No datasets to display.") continue;
            // EvalKit list writes informational logger records to stdout alongside the rows.
            if (Regex.IsMatch(line.Trim(), @"^\[[0-9:.]+\]\s+\[CLI\]\s+\[info\]\s+")) continue;
            var match = Regex.Match(line, "^\\s*\"(?<name>[^\"]+)\"\\s+v(?<version>\\S+)(?:\\s+(?<summary>.*))?\\s*$");
            if (!match.Success) throw new InvalidDataException("Unsupported EvalKit catalog output contract. No dataset identity was guessed.");
            entries.Add(new(match.Groups["name"].Value, match.Groups["version"].Value, match.Groups["summary"].Value.Trim(), container));
        }
        return entries;
    }
}
