# Semantic_ModelEvaluator_Kapil_V0 — Assessment, Architecture, UX Specification, Plan

## Rewrite baseline (2026-11-07)

The previous implementation is archived under `artifacts\pre-rewrite-20261107-235537.zip`
(50 source, test, documentation, sample and configuration files; build outputs excluded).
No datasets, model caches, external checkouts or IDE settings were removed.
The current starting point is a partial Core/Providers implementation, unverified tests,
and an App project without a UI, not the original empty template described below.
Existing engine code is candidate reuse and must pass validation before acceptance.
The WPF application is being built anew. P0/P1 acceptance takes precedence over broad
unverified provider support. The architecture and UX sections below describe the target,
not a claim that every feature has been implemented.

### Verified rewrite checkpoint (2026-11-08)

| Milestone | Changed files / functionality | Evidence |
|---|---|---|
| P0 foundation | App startup/DI, manifest, theme resources, service-backed asynchronous commands; corrected engine compatibility and input checks; preserved Core/Providers boundaries | Visual Studio solution build successful; command-line builds report zero warnings/errors |
| P1 working comparison | `App\ViewModels\WorkbenchViewModel.cs`, `MainWindow.xaml`, dialogs and mapping window; two models, live thresholds, inspection, scenarios, save/open/export | 70 offline tests passed, zero failed; UI Automation passed on Debug and on the self-contained Release executable |
| P2 comparator baseline | `Controls\AnalysisChart.cs`, comparison views, reference switching, heatmap/radar/curves/distributions, errors, performance disclosures, ranking presets/custom weights | Hand-computed metric/reference tests; end-to-end view-model round trip and rendered comparator smoke |
| P3 provider subset | ONNX recipe/hash/cancellation corrections, HF immutable acquisition, credential settings, explicit FFA worker bindings | Separate opt-in native test passed for two pinned public models; fake-channel provider tests pass; live Microsoft inference remains unverified |
| P4 packaging baseline | `build\Publish.ps1`, `build\Smoke.ps1`, `README.md`; regression tests for privacy, invalid predictions, identity and safe cache cleanup | Self-contained win-x64 ZIP produced; packaged executable completed the desktop smoke workflow |

Native verification used:

- `sentence-transformers/all-MiniLM-L6-v2@1110a243fdf4706b3f48f1d95db1a4f5529b4d41`
- `BAAI/bge-small-en-v1.5@5c38ec7c405ec4b44b94cc5a9bb96e735b38267a`

Both ran locally on the synthetic helpdesk dataset; this verifies inference and persistence,
not real-world quality. The workbook and report are in `artifacts\native-verification`.
The normal suite deliberately skips this network test (70 passed, 1 skipped); the separate
opt-in run passed it (1 passed, 0 failed).

Release: `artifacts\package-20261108-001233\Semantic_ModelEvaluator_Kapil_V0.exe`.
Archive: `artifacts\Semantic_ModelEvaluator_Kapil_V0-win-x64-20261108-001233.zip`.
SHA-256: `A403204051994C20837FF30D09A29BCC7F416D10DC3F25DA04E93EC9F570315B`.

**Remaining scope:** cloud connectors; broader provider/device qualification; task/modality
filter controls and explicit revision-picking UX; richer model-card metadata; cross-run
performance comparability gating; large-data benchmarks; comprehensive screen-reader
verification; signed installer/update delivery. The Runner currently presents seven review
sections, not a step-gated wizard. Native image/generative/classifier execution is not claimed.
The next milestone is provider qualification and UX/accessibility hardening, not additional
unverified metrics. No production-readiness claim is made.

**Scientific corrections:** Precision@K uses K as its denominator (definition v2);
average precision groups tied scores (v2); duplicate/non-finite predictions are rejected;
changed datasets do not supply substitute judgments on restore; new UI runs persist metric
definition versions; model hashes include tokenizer/recipe inputs rather than graph timestamps alone.

## A. Repository assessment

| Source | What exists | Reuse decision |
|---|---|---|
| **FuzzyFindAnything** (`sumghosh_microsoft/FuzzyFindAnything`, cloned read-only to `..\..\_smeval_refs`) | .NET 10 WPF search app. `Core` (SQLite catalog, DiskANN P/Invoke, provider protocol `ExternalModels`), `Inference` (`NativeModel`: EmbeddingGemma ONNX, SentencePiece, WPF-based `ImagePipeline`), `HuggingFaceInference` (JSON-line worker hosting compatible HF ONNX models: BERT WordPiece / BPE / CLIP tokenizers, mean pooling, L2 normalisation), native DiskANN via CMake + MKL + Boost. | **Not referenced as binaries.** `Directory.Build.props` forces `win-x64`, Core P/Invokes a natively-built DiskANN DLL, and `Inference`/`HuggingFaceInference` set `UseWPF`. Instead we reuse (1) its **out-of-process JSON-line protocol** (`--provider-request` catalog/inspect/prepare and `--model <dir> --threads <n>` workers with `query` / `documents` / `capabilities` / `shutdown`, snake_case) through `FfaProvider`, and (2) its proven inference recipe (WordPiece tokenisation, attention-masked mean pooling, L2 normalisation) re-implemented in `LocalOnnxProvider` without WPF. DiskANN is **optional and not used**: evaluation datasets are scored with exact brute-force cosine search, which is the correct reference for metric computation (ANN recall loss would contaminate model comparisons). |
| **FuzzyFindAnything-MicrosoftProvider** | Self-contained ARM64/x64 executable over PerceptiveShell native APIs (SpaceV6 100-d, Palermo 768-d, Florence 768-d text+image, Genoa 1024-d). Needs internal Azure Artifacts feed, MSVC, private GitHub release via `gh`. Registered at `%LOCALAPPDATA%\FuzzyFindAnythingProviders\microsoft\provider.json`. | **Optional adapter only.** `FfaProvider` discovers any FFA registration, calls `catalog`, and drives prepared models through the worker protocol. No endpoints, feeds or credentials are hard-coded; when absent the hub shows *Not installed* with the reason. |
| **EvalKit.Viewer** (`ASG-SearchPlatform/Tools/EvalKit.Viewer`, local checkout) | Blazor Server app: Overview (scenario chips, heatmap), Compare (scorecards), Threshold Explorer (slider + numeric entry, curves, radar, per-query side pane), JSON workbooks with dependency checks, dataset cache. | **Interaction patterns re-implemented** natively in WPF (no code copied): single threshold value shared by slider/numeric box, recalculation from cached scores only, scenario chips with a switchable reference, `delta = scenario − reference`, radar on a common display scale with raw values preserved, workbook warnings for missing dependencies. |
| Existing workspace | Empty WinUI class-library template (`Class1.cs`). | Replaced by the solution below (the `.slnx` file is kept so the open Visual Studio solution stays valid). |

The *EvalKit Threshold Explorer – First-Time Setup Guide.docx* was not found on disk; the behaviour is derived from the viewer source and the requirements.

## B. Target architecture

```
Semantic_ModelEvaluator_Kapil_v0.slnx
├─ src/SemanticModelEvaluator.Core        (net10.0, no UI, no native deps)
│   Models/        ModelIdentity, ModelDescriptor, EvalTask, Modality, Readiness, ProviderHealth
│   Providers/     IModelProvider, ITextEmbeddingRunner, ProviderRegistry
│   Datasets/      EvalDataset, TabularReader (CSV/JSON/JSONL), ColumnRole, MappingProfile,
│                  MappingSuggester, DatasetBuilder, DatasetManifest (.smds.json), DatasetValidator
│   Evaluation/    EvaluationRun, ModelRunResult, EvaluationEngine, PredictionImporter, EnvironmentInfo
│   Metrics/       MetricDefinition, IMetric, MetricRegistry, retrieval / classification /
│                  similarity / performance metric plug-ins, ThresholdEngine, ScoreHistogram
│   Comparison/    Scenario, ComparisonEngine (reference deltas), RadarProjection, ErrorAnalysis,
│                  RankingEngine (explicit weights, exclusions, missing-data policy)
│   Persistence/   Workspace (.smeval), WorkspaceService (atomic save, dependency checks), AppPaths
│   Export/        ReportExporter (CSV, JSON, HTML, text) with sensitive-text opt-in
│   Security/      Redactor, AppLog, NetworkPolicy
├─ src/SemanticModelEvaluator.Providers   (net10.0-windows; ONNX Runtime, Microsoft.ML.Tokenizers)
│   DeterministicProvider   hashed n-gram baselines (labelled synthetic, used by tests)
│   LocalOnnxProvider       BERT-family sentence-embedding ONNX folders
│   HuggingFaceProvider     search, revisions, licence/gating, pinned-revision download, hashes
│   FfaProvider             FuzzyFindAnything / MicrosoftProvider JSON-line adapter
│   ImportedPredictionsProvider  predictions/scores files as "models"
│   CredentialStore         Windows Credential Manager (no tokens on disk in plain text)
├─ src/SemanticModelEvaluator.App          (WPF, MVVM Toolkit, DI) → Semantic_ModelEvaluator_Kapil_V0.exe
│   Shell, ModelHub, DatasetStudio, EvaluationRunner, Explorer, Comparator, Workbook, Settings
│   Controls/ LineChart, RadarChart, Histogram (custom-rendered, keyboard + automation names)
│   Themes/ Light.xaml, Dark.xaml, Styles.xaml
└─ tests/SemanticModelEvaluator.Tests      (xUnit; deterministic fixtures in samples/)
```

Dependency rule: `App → Providers → Core`; `Core` never references ONNX, WPF or the network. Every long operation is `async`, takes a `CancellationToken`, and reports `IProgress<T>`.

### Two-model comparison components (Enhance phase)

| Concern | Component |
|---|---|
| Provider adapters | `Providers/FoundryProvider` (Entra ID via `Azure/AzureAuth` DefaultAzureCredential, or Credential-Manager API key; transient-only retry, 401/403 → `AccessDeniedException`, usage + correlation ids), `PsapiProvider` (captured-run discovery; live invocation blocked — no documented contract), `HuggingFaceProvider`, `LocalOnnxProvider` |
| Model configuration | `Core/Pairing/PairComparison.cs`: `ModelConfiguration` (provider-neutral; provider metadata kept in `Parameters`), `PairSelection` (cross-provider pairs, identical-config rejection) |
| EvalKit results | `Core/EvalKit/EvalKitFormat` (metrics envelope v3, query-results JSONL, dataset manifest with sha256 checks), `Providers/EvalKit/EvalKitCloud.cs` (`EvalKitCloudService`, `IResultContainer`, AzureCliCredential only, sensitive opt-in without fallback, `VerifyBundle`; `EvalKitCli`) |
| Version validation | `PairComparer.Compare` blocks dataset name/version/payload-hash mismatches per side |
| Orchestration | `EvaluationEngine` (bounded concurrency, item-specific retry with consecutive-failure cap, partial results) |
| Metrics / comparison | `MetricRegistry`, `ThresholdEngine`, `PairComparer` (rows, outcomes, counts, curves, recommendation, limitations) |
| Report | `Core/Export/DetailedReportBuilder` (21 sections, CSP, inline SVG with text tables, encoded/redacted, sensitive confirmation) |
| UI state | `App/ViewModels/PairComparisonViewModel` (dataset container choice, inclusion rule, dimensions, sample limit), `PairSideViewModel` (batch metadata, connection/validation status) |
| Security | `Core/Security/Security.cs` (`Redactor` incl. `RedactValue`/`IsSecretName` for credential-named parameters, `AppLog` with correlation ids and no payloads, `NetworkPolicy`) |
| Dataset containers | `Providers/EvalKit/EvalKitCloud.cs` `EvalKitDatasetContainer` (published/working × sensitivity, blob prefix) and `EvalKitCli.FetchEnvironment` (headless, pinned container, no fallback) |
| Accessibility | `App/Controls/AnalysisChart` (automation peer with text summary; per-series dash pattern and marker shape, not colour alone) |

Comparison scope order in `PairComparer.Compare`: eligibility on the full dataset → restrict to `SampledIds` → judged-only inclusion rule → metric dimensions (quality excluded ⇒ no per-item winners) → rows, counts, curves, recommendation. Retry policy: `EvaluationEngine.IsItemSpecific` only for HTTP 400/413/422.

Contract sources: EvalKit file formats, storage defaults and the Foundry `/openai/v1/embeddings` pattern were derived from the authorized local checkout `ASG-SearchPlatform` (EvalKit.Viewer, EvalKit.CLI, DataPipelines). No PSAPI endpoint/auth contract was found, so none is implemented.

**Data flow.** Provider → `ModelDescriptor` → `EvaluationEngine` loads a runner → embeddings (cached on disk by model-hash + text-hash) → exact cosine top-N per query → `ModelRunResult` (IDs + scores + latencies, *no dataset text*) → persisted after each model. `ThresholdEngine` recomputes metrics from those cached scores; inference is never re-run for threshold or K changes.

## C. UX specification

* **Shell** — left navigation (Model Hub, Dataset Studio, Run Evaluation, Explorer, Comparator, Workbook & Export, Settings & Diagnostics), header with workspace name, dirty marker, Save/Open, theme toggle. Status bar shows network mode (Online / Offline) and background activity.
* **Model Hub** — source filter + task/modality filters + search; cards show name, provider, family, revision, task, modalities, dimension, quantisation, runtime, size, licence, local availability, readiness and reason. Actions: Search Hugging Face (explicit network disclosure), Download (pinned revision), Register local ONNX folder, Import predictions, Validate, *Add to comparison* (no model-count limit). Compatibility of the selection with the current dataset/task is shown live.
* **Dataset Studio** — open manifest or sample; import CSV/JSON/JSONL table → preview → per-column role mapping. Suggestions are only made for exact well-known column names and must be confirmed; ambiguous mappings block the build. Save/load mapping profiles; validation warnings, judgment coverage, sample queries, image thumbnails, data classification.
* **Run Evaluation** — 7-step wizard (models, dataset, task, metrics, execution, review, run). Review lists versions, mapping, runtime, downloads, network activity and privacy implications. Run screen shows current model, completed/remaining, elapsed, warnings, failures, Cancel. Completed models are kept when another model fails.
* **Explorer** (primary) — left: runs, models (visibility + focus), saved scenarios, query filters. Centre: model chips, metric cards with delta vs reference, threshold bar (slider, exact number, Reset, Link thresholds, K, Save scenario), live retained/empty counts, threshold curve (click to set threshold), score distribution, virtualised per-query grid with expandable side-by-side ranked results (retained vs filtered, judgments, harm, scores, latency, thumbnails). Right: metric definition, model and dataset details, scenario notes; collapsible and resizable.
* **Comparator** — scenario chips with reference selector; tabs: Metric grid (value, Δ, direction, n, judged, missing), Radar (only bounded metrics; transformations listed), Heatmap (Δ with numeric labels, colour-blind-safe diverging palette), Threshold curves, Distributions, Error analysis (shared / model-specific misses, empty, FP/FN), Qualitative (same query across models), Performance (with hardware/runtime disclosure), Ranking (presets, editable weights, formula, exclusions).
* **Workbook & Export** — notes, save/save-as/open/duplicate, dependency warnings, CSV/JSON/HTML/text export; example text excluded unless explicitly opted in.

## D. Implementation plan

| Milestone | Scope | Acceptance |
|---|---|---|
| P0 | Solution, branding, contracts, providers, persistence | `dotnet build` clean; provider discovery/missing-provider tests pass |
| P1 | Vertical slice: two models × sample dataset → metrics → threshold → side-by-side → save/restore | Real run with two Hugging Face ONNX models and deterministic baselines; workspace round-trip test |
| P2 | Comparator: multi-model grid, curves, heatmap, radar, per-example inspection, scenarios, reference switching | Comparison/reference tests; UI smoke launch |
| P3 | HF search/download/cache, FFA/MicrosoftProvider adapter, imported predictions, classification task | Adapter tests with in-memory channel; import tests |
| P4 | Hardening: redaction, offline mode, cache cleanup confirmations, accessibility IDs, self-contained publish | Privacy/redaction/accessibility tests; `build\Publish.ps1` produces runnable zip |

### Known V0 limits (by design, surfaced in the UI)
* Native execution: BERT-WordPiece sentence-embedding ONNX models (text). Image, VLM, generative, reranker and safety models are evaluated through **imported predictions/scores**; their cards say so.
* Hardware acceleration: CPU execution provider only in V0; available ORT providers are displayed.
* Datasets are loaded in memory. No large-dataset capacity or latency guarantee has been benchmarked.
