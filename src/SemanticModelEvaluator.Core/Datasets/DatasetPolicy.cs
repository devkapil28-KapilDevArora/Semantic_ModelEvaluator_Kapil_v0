namespace SemanticModelEvaluator.Core.Datasets;

public enum DatasetSource { Local, HuggingFace, MicrosoftInternal }
public enum DatasetOperation { Preview, Evaluate, ExportSummary, ExportSamples, ExternalProcessing }

/// <summary>Optional, additive metadata. Legacy local manifests and workbooks remain readable.</summary>
public sealed record DatasetProvenance
{
    public DatasetSource Source { get; init; }
    public string? Repository { get; init; }
    public string? Revision { get; init; }
    public string? Configuration { get; init; }
    public string? Split { get; init; }
    public string? License { get; init; }
    public string? Subset { get; init; }
    public List<string> Components { get; init; } = [];
    public bool RequiresApproval { get; init; }
    public string? Restriction { get; init; }
}

public static class DatasetPolicy
{
    private static readonly string[] RaiFamilies =
        ["peopleanimals", "nyuuzyou", "personalgallery", "webimagespersonal", "onedriverai", "imageembeddingrai",
            "uhx", "idenprof", "coco2017", "openimagesextended"];

    public static bool IsGovernedFamily(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return RaiFamilies.Any(normalized.Contains);
    }

    public static DatasetProvenance EvalKit(string name, string classification) => new()
    {
        Source = classification.StartsWith("Public", StringComparison.OrdinalIgnoreCase) ||
                 classification.StartsWith("Synthetic", StringComparison.OrdinalIgnoreCase)
            ? DatasetSource.Local : DatasetSource.MicrosoftInternal,
        RequiresApproval = IsGovernedFamily(name) ||
            classification.StartsWith("Sensitive", StringComparison.OrdinalIgnoreCase),
        Restriction = "Privacy/CELA approval, approved use, retention and export evidence have not been verified. Storage access is not approval."
    };

    public static void Ensure(EvalDataset dataset, DatasetOperation operation) =>
        Ensure(dataset.Provenance, operation);

    public static void Ensure(DatasetProvenance? provenance, DatasetOperation operation)
    {
        if (provenance is null) return;
        if (provenance.RequiresApproval)
            throw new InvalidOperationException("Dataset approval pending: " + provenance.Restriction);
        if (provenance.Source == DatasetSource.MicrosoftInternal &&
            operation is DatasetOperation.ExportSamples or DatasetOperation.ExternalProcessing)
            throw new InvalidOperationException("Internal sample export and external processing are blocked until an authoritative policy grants this data flow. No raw internal images are exported.");
    }
}
