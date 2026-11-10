# Semantic_ModelEvaluator_Kapil_V0

A local-first Windows model evaluation workbench built with .NET 10 and WPF.
It is a working V0, not a claim that every architecture or accelerator can execute natively.

## Launch

Extract the self-contained `Semantic_ModelEvaluator_Kapil_V0-win-x64-*.zip`,
then run `Semantic_ModelEvaluator_Kapil_V0.exe`. Keep the extracted folder intact.
No Visual Studio, Python, Node.js or separately installed .NET runtime is required.
The portable package is unsigned; enterprise application-control policies may require approval.

## First comparison

1. In **Dataset Studio**, click **Load synthetic demo**. This selects two deterministic lexical baselines.
2. In **Run Evaluation**, review the selected models, dataset, metrics and CPU settings, then run.
3. In **Explorer**, select a scenario, enable its threshold and use the slider or numeric field.
   Metrics operate on cached scores; inference is not repeated. K cannot recover results beyond
   the run's retained depth. The comparison grid includes values, deltas, counts and missing-data notes.
4. Open **Inspect examples** for inputs, ground truth, retained/filtered results and side-by-side outputs.
5. **Save as scenario** creates a separate threshold configuration. **Comparator** switches the
   reference and shows grid, heatmap, radar, curves, distributions, errors, performance and ranking.
6. Save a `.smeval` workbook, reopen it, or export JSON, CSV, text or HTML.

The bundled samples are synthetic. Even when evaluated with real neural models, these samples
do not establish real-world model quality or a universal best model.

## Compare two models (tab "00 Compare two models")

A six-step flow: **1 Choose source → 2 Choose two models → 3 Choose dataset and evaluation →
4 Run or load comparison → 5 Review results → 6 Download detailed HTML.**

Each side (Model A, Model B) independently uses one source:

| Source | What happens | Model invoked? |
|---|---|---|
| Provider model (Foundry, Hugging Face, local ONNX, deterministic) | New evaluation on the selected dataset | Yes (remote providers ask for confirmation first) |
| EvalKit local run folder | Reads `*.metrics.json` + `*.query-results.jsonl` | No |
| EvalKit cloud (non-sensitive / sensitive) | Lists jobs, downloads **one** hash-verified bundle | No |
| Cached EvalKit bundle | Re-verifies size + sha256 of every file, then reads | No |
| Saved run of this app | Reuses persisted scores | No |

PSAPI models (Genoa, Florence, Palermo, Space v6) run **live and locally** as an EvalKit job. The app
starts the EvalKit CLI, which starts the `EvalKit.Backend.Psapi` external backend from
ASG-SearchPlatform `develop`. That backend runs on CPU from local packages, with no endpoint and no
runtime credential. Each side shows whether the backend is ready, and if it is not, the exact build
step that is missing. Text models (Space v6, Palermo) need a text dataset; image models (Florence,
Genoa) need an EvalKit image dataset. A dataset with the wrong modality is blocked before the run.
Captured PSAPI runs in verified EvalKit bundles also load. Contract evidence, limits and open
questions are in [docs/PSAPI_DISCOVERY.md](docs/PSAPI_DISCOVERY.md).

The Hugging Face selector lists only semantic-search embedding models (retrieval and embedding
output; never generative, classification or reranking models).

Comparison eligibility (shown in the UI and the report): both sides must reference the same dataset
name, exact version and payload hash; otherwise the comparison is blocked with the reason. Identical
configurations (same provider, model, revision, endpoint, parameters and content hash) are rejected.
Unjudged items are **unknown** — never relevant, safe or passing. Thresholds are per side (score
scales differ) and only **recalculate** metrics from captured scores; runs whose source recorded
positions only are labelled as rank cut-offs.

**Download detailed HTML** writes one self-contained file (no CDN, CSP-restricted, encoded content,
redacted secrets, **28 numbered, collapsible sections** with a table of contents, sortable tables,
expand/collapse all, and report schema version 2.0). Endpoint values are never exported. Metrics
include Recall/Precision/NDCG/MRR/**MAP@K**, hit and empty rates, rank-only agreement between the
two models (top-K overlap and Spearman rank correlation), and P50/P95/P99 latency when there are
enough samples (otherwise unknown). A compatibility table covers dataset identity, modality, score
scale, dimensions and truncation. For sensitive or unclassified data, per-item content requires explicit
confirmation; otherwise the report contains aggregate counts only (no item IDs or scores), and any
known query/document identifier inside free-form warnings is masked as `[item]`. Model parameters
whose names look like credentials (`*key`, `*secret`, `*token`, `password`, connection strings) are
suppressed. SVG charts are culture-invariant and each threshold chart has a complete data table.
The report separates *report generated* (export time) from *comparison computed*, and shows the
source commit when the build embeds one.

Step 4 settings: per-side score threshold, K, sample limit (the comparison is restricted to the
sampled ids), **inclusion rule** (all items, or judged items only), **evaluation dimensions**
(quality, harm, coverage/distribution — when quality is not selected no per-item winner is declared),
and reuse of captured results versus a new evaluation. Model parameters come from provider
configuration (e.g. Foundry `Dimensions`, ONNX pooling recipe) and are shown read-only.

Step 3 dataset fetch: choose the **dataset container** — EvalKit default store, or published/working
× non-sensitive/sensitive (`eval-*-published`, `eval-*-working`; working uses
`EVALKIT_BLOB_PREFIX=evalkit/staging/datasets`). The CLI runs headless with your own sign-in; a
named container is always pinned with its storage account and is never replaced by another
container or identity. The dataset's published/working state and classification are shown and
flow into the report's handling notice. EvalKit runs also show batch metadata (experiment, run,
EvalKit version, backend, counts, depth).

### Configuration

Settings are stored in `%LOCALAPPDATA%\Semantic_ModelEvaluator_Kapil_V0\settings.json`
(edit through **Settings → Provider settings**). Secrets are never stored there.

* **Microsoft AI Foundry** — `FoundryDeployments[]`: `Name`, `Endpoint` (`https://<resource>.openai.azure.com`
  or `.services.ai.azure.com`), `Deployment`, `ApiStyle` (`OpenAIv1` or `AzureOpenAIDeployment`),
  `ApiVersion`, `ModelVersion`, `Dimensions`, `Auth` (`EntraId` = DefaultAzureCredential, default;
  or `ApiKey` = key in Windows Credential Manager under `foundry:<Name>`), `TenantId`, `TokenScope`,
  optional `PricePer1KInputTokens` (cost is shown only when this is set).
* **EvalKit** — `EvalKit.ResultsAccount` (default `scaievalkitdatasets`), `NonSensitiveResultsContainer`
  (`eval-nonsensitive-results`), `SensitiveResultsContainer` (`eval-sensitive-results`), `TenantId`,
  `CliPath`/`RepositoryRoot` (EvalKit CLI for dataset fetch), `ResultCacheDirectory`, `DatasetCacheDirectory`.
  Environment overrides: `EVALKIT_VIEWER_RESULTS_ACCOUNT`, `EVALKIT_VIEWER_CACHE_DIR`, `EVALKIT_CACHE_DIR`,
  `AZURE_TENANT_ID`. Cloud access uses your `az login` identity (AzureCliCredential) only; a denied
  sensitive container is reported and never retried with another identity or container.
* **Hugging Face** — optional gated-model token in Credential Manager via Provider settings.

```powershell
az login                                   # EvalKit cloud results / Entra-ID Foundry
dotnet build Semantic_ModelEvaluator_Kapil_v0.slnx
dotnet run --project src\SemanticModelEvaluator.App
```

## Your data and models

**Datasets:** open a `.smds.json` manifest or use **Map CSV / JSON / JSONL**. Choose the task,
map each table, confirm the mapping, then validate/save the manifest. Mapping profiles can be
saved and loaded. Source files are not changed. Paths in a locally created manifest are absolute;
move the data deliberately and update the manifest when transferring it.

Retrieval uses queries (`id`, `text`), documents (`id`, `text`), and optional judgments
(`query_id`, `doc_id`, `grade`, optional `harmful`). Classification/safety uses examples with
an explicit label mapping and an explicit positive label for binary scores.
Similarity uses paired text and gold similarity. Image paths can be mapped and previewed;
native image inference is not included.

**Predictions:** load the dataset first, then **Import predictions**. Retrieval files require
`query_id,doc_id,score` (or `rank`); example tasks require `example_id` plus `score`,
`predicted_label` or `output`. `latency_ms` is optional and is not certified as local measurement.
Duplicate predictions and non-finite numeric values are rejected. Missing predictions are
reported as missing, not as negative or safe outcomes.

**Hugging Face:** search explicitly, inspect the license and compatibility, select/download models,
then run locally. Revisions resolve to exact commit SHAs. The native adapter supports BERT WordPiece
ONNX sentence embeddings with explicit mean/CLS pooling recipes and named embedding outputs.
Other architectures are import-only. No repository Python code is executed.
Gated-model tokens are entered through **Provider settings** and stored in Windows Credential Manager.
Cached models work offline. The real-model verification covers `all-MiniLM-L6-v2` and
`BAAI/bge-small-en-v1.5` on CPU.

**Local ONNX:** register a folder containing a supported graph, `vocab.txt`, model/tokenizer
configuration and `1_Pooling/config.json` (or an explicit `smeval-model.json` pooling recipe).
Folder inspection does not write into the original folder. Hashes include graph, vocabulary,
configuration and embedding recipe; changed files are not silently substituted.

**Microsoft / FuzzyFindAnything:** optional, not bundled. Install, authenticate and prepare models
in the authorized provider application, then bind the trusted worker executable and model folder
through **Provider settings**. The protocol is fake-channel tested; live Microsoft inference has
not been verified on this machine. No credentials or internal endpoints are embedded.

## Interpretation and privacy

- Precision@K divides the number of relevant retained results by K, including missing ranks.
  Recall, MRR and NDCG exclude queries without relevant judgments. See metric definitions for denominators.
- Unjudged retrieved documents count as non-relevant for quality metrics; Judged@K discloses coverage.
  Harmful-result rate excludes unjudged results, never treating them as safe.
- PR-AUC is average precision grouped by tied score thresholds. ROC-AUC requires both classes.
- Thresholds are scenario-specific: score scales need not be comparable across models.
- Radar uses only fixed 0-1 directional metrics; lower-is-better values become `1-value`.
- Ranking lists its formula, weights and exclusions. Missing weighted values are never imputed.
- Throughput with cache enabled includes cache retrieval, not just inference.
  Process peak memory includes the application and is not isolated model memory.
- Workbooks retain cached predictions, generated outputs and analyst notes. Treat them as sensitive.
  Exports exclude example text by default; inclusion requires explicit confirmation.
- Restoring a changed/missing dataset preserves results but blocks recalculation with incorrect judgments.
  Keep the original dataset files for reproducibility.

## Build and verification

```powershell
dotnet build Semantic_ModelEvaluator_Kapil_v0.slnx
dotnet test tests\SemanticModelEvaluator.Tests\SemanticModelEvaluator.Tests.csproj
powershell.exe -NoProfile -File build\Smoke.ps1
powershell.exe -NoProfile -File build\Publish.ps1
```

The normal suite is offline. Optional real-model verification downloads public model assets:

```powershell
$env:SMEVAL_VERIFY_NETWORK = '1'
dotnet test tests\SemanticModelEvaluator.Tests\SemanticModelEvaluator.Tests.csproj --filter FullyQualifiedName~NativeVerificationTests
```

Optional live PSAPI verification runs Space v6 through a locally built EvalKit CLI and PSAPI backend
(CPU only, no network):

```powershell
$env:SMEVAL_VERIFY_PSAPI = '1'
dotnet test tests\SemanticModelEvaluator.Tests\SemanticModelEvaluator.Tests.csproj --filter FullyQualifiedName~PsapiLiveVerificationTests
```

The UI smoke script launches its own process with isolated `--data-root` storage under `artifacts`,
exercises the demo workflow and stops only that process. Native verification writes its
workbook and JSON summary to `artifacts\native-verification`.

## Scope and remaining work

Implemented: the P1 vertical slice, multi-model/scenario analysis, explicit table mapping,
native CPU embeddings, prediction imports, workbooks and exports, plus self-contained packaging,
and the two-model comparison flow with EvalKit local/cloud results, Foundry and the detailed HTML report.

Limitation status:

| Item | Category | Status |
|---|---|---|
| Live Microsoft AI Foundry embeddings | Implemented but not validated — blocked by missing credentials | Fake-endpoint tested (auth, retry, usage, errors) |
| PSAPI live invocation | Implemented and validated (Space v6, local CPU via the EvalKit CLI) | Florence/Genoa need an EvalKit image dataset and Palermo uses an experimental package; these were not run live here. The owner and reuse terms are open questions (see docs/PSAPI_DISCOVERY.md). |
| EvalKit cloud listing/download | Implemented but not validated — blocked by missing access | In-memory container tested, incl. sensitive denial without fallback |
| EvalKit CLI dataset fetch (published/working containers) | Implemented but not validated — blocked by missing access / CLI build | Arguments, environment and no-fallback rules unit tested |
| *EvalKit Threshold Explorer – First-Time Setup Guide.docx* | Blocked by missing access (document not available) | Contracts derived from the authorized local ASG-SearchPlatform checkout |
| User-editable per-run model parameters | Not implemented | Parameters come from configuration/manifest and are shown read-only |
| Very large datasets | Implemented but not validated | Corpus loaded in memory (1 GB file guard, cancellable hashing); captured results streamed. Not benchmarked |
| Image thumbnails from cloud datasets | Unsupported by the current data schema | Only existing local files are shown; remote image URIs are never fetched |
| Token usage / cost for captured runs | Unsupported by the current data schema | EvalKit results do not record usage; shown as unknown. Cost only with configured price |

Design decisions: per-item retries apply only to item-specific HTTP 400/413/422 (429/5xx use the
transient policy; 401/403 are never retried). Persisting partial results after cancellation uses
`CancellationToken.None` deliberately so completed work is not lost. A dataset whose content hash
differs is treated as a different version even when the version label matches.

Not release-certified: native image/generative/classifier execution,
GPU/NPU execution, large-dataset scaling, live internal-provider authentication/inference,
screen-reader usability beyond automation identifiers, signed installer/update delivery.
The Runner is a seven-section review flow rather than a modal step-by-step wizard.

The pre-rewrite source is preserved in `artifacts\pre-rewrite-20261107-235537.zip`.
Reference repositories, user datasets, model caches and Visual Studio settings were not deleted.
