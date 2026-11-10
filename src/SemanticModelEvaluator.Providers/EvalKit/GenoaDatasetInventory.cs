namespace SemanticModelEvaluator.Providers.EvalKit;

public sealed record GenoaDatasetFamily(string Name, string Purpose, string Readiness, string NextStep);

/// <summary>Source-linked planning inventory, not registered identities or authorization grants.</summary>
public static class GenoaDatasetInventory
{
    public const string ScenarioUrl = "https://microsoft.visualstudio.com/OS/_workitems/edit/64797876";
    public const string AccessUrl = "https://coreidentity.microsoft.com/manage/Entitlement/entitlement/asgwspevalsd-dt4t";
    public const string FollowUpUrl = "https://teams.microsoft.com/l/message/19:19cb81f9-b338-4ba5-a370-bf925cc44a1d_63eb5371-8424-440c-b317-0d6447e588c1@unq.gbl.spaces/1793777113915?context=%7B%22contextType%22:%22chat%22%7D";

    public static IReadOnlyList<GenoaDatasetFamily> Families { get; } =
    [
        new("People-Animals", "Dehumanization / incorrect person-animal retrieval",
            "CELA and Privacy pending in scenario and follow-up.",
            "Verify generated captions are absent; tasks 64250839 / 64689539; source.json and publication evidence required."),
        new("Nyuuzyou web images", "Web images / recognizable people",
            "CELA reported approved; Privacy pending.",
            "Remove minors, visible names/handles and Telegram IDs; Privacy review and source.json (64365080)."),
        new("GenData Personal Gallery / Web Images Personal", "Representative personal-image retrieval",
            "CELA reported approved. Later notes differ on Privacy; not verified for a specific version.",
            "Attach approval evidence and reconcile Privacy status; source.json (64365080). Prioritize realistic queries and judgments."),
        new("OneDrive RAI / image_embedding_rai", "RAI queries; non-sensitive and sensitive subsets",
            "Face/subset provenance and CELA coverage unresolved; Privacy pending.",
            "Verify each subset's source, approvals and sensitivity; source.json (64365080)."),
        new("UHX Alpha / Gamma", "Internal Recall screenshots; partial fit for personal-image search",
            "Original Recall Privacy and CELA evidence unresolved.",
            "Find original consult/vendor coverage and obtain current-use approval; Working access requires the full reader tier."),
        new("idenprof", "Occupation image coverage",
            "ADO lists review pending; Nov 4 follow-up reports CELA and Privacy approved.",
            "Attach authoritative approval/provenance evidence to source.json before treating a registered version as ready."),
        new("COCO 2017", "Everyday images / general retrieval benchmark",
            "Reported approved; exact snapshot and source evidence still required.",
            "Add approval evidence to source.json (64365080). Public HF mirrors are separate sources, not this approved internal snapshot."),
        new("Open Images Extended", "Broader image category coverage",
            "Reported approved; exact snapshot and source evidence still required.",
            "Add approval evidence to source.json (64365080); resolve a registered name/version from the authorized catalog.")
    ];

    public static string Setup => "Sources: ADO scenario revision 12 (2026-11-03), WorkIQ Nov 4 follow-up and EvalKit setup guide. " +
        "This is a dated inventory; current catalog versions and approval evidence must be checked.\n" +
        $"Scenario: {ScenarioUrl}\nFollow-up: {FollowUpUrl}\nData access: {AccessUrl}\n" +
        "Build EvalKit.CLI from the authorized ASG-SearchPlatform checkout; configure its path in Credentials / worker bindings > EvalKit; run az login. " +
        "Dataset storage: scaievalkitdatasets; select the published/working and sensitive/non-sensitive container explicitly. " +
        "NuGet feed access, dataset reader access and results-container access are separate.\n" +
        "Documented example: Image Embedding RAI 001 (English) v1.0.3 in eval-nonsensitive-published. " +
        "This is a reference identity, not proof of current availability or approval; resolve it via List authorized dataset versions.\n" +
        "New evaluations may fetch a selected catalog version without an existing run. Saved-run comparisons still require the exact payload hash. " +
        "No raw internal images are exported or sent to Hugging Face.";
}
