# Enhancement baseline and preservation matrix

Established 2026-11-08 before enhancement implementation. This is an additive change to
**Semantic_ModelEvaluator_Kapil_V0**, not a rewrite. Pre-existing uncommitted changes are
preserved. Four projects: Core (`net10.0`), Providers and Tests (`net10.0-windows`), and
App (WPF, `net10.0-windows`). App uses Microsoft DI and MVVM Toolkit.

Release solution build: 0 warnings, 0 errors. Baseline: **146 passed, 2 opt-in tests
skipped, 0 failed**. Visual Studio Debug build was blocked by the running application's
file locks; that process was not stopped.

## Feature-preservation matrix

| Capability | Current implementation / owner | Dependencies and evidence | Additive change | Regression risk |
|---|---|---|---|---|
| Model discovery | Providers: deterministic, local ONNX, HF, FFA/MicrosoftProvider, imported outputs, Foundry, PSAPI | ProviderRegistry; provider/health tests | Registered PSAPI builds; source filters | Identity collisions, unavailable builds |
| Local text inference | Providers ONNX/WordPiece and deterministic runners; Core EvaluationEngine | ONNX Runtime 1.22.0; earlier two-model live verification; engine tests | Keep existing runner; add whole-dataset runner boundary | Cancellation, caches, performance attribution |
| MicrosoftProvider | Optional FFA out-of-process worker binding | Contract tests; live Microsoft worker not qualified | Preserve configuration and discovery | Native dependency isolation |
| Image/multimodal execution | PSAPI Florence/Genoa pair-job path; imported image outputs; image paths/previews | No general native image runner in baseline | Normalize image datasets and allow compatible whole-dataset PSAPI execution | Wrong modality, image disclosure |
| Dataset Studio | App screen 02; Core CSV/TSV/JSON/JSONL mapping and `.smds.json` | Mapping validation, dataset and view-model tests | Add Dataset Hub, not replace Studio | Mapping, identity, changed judgments |
| Bundled datasets | Synthetic helpdesk retrieval, safety classification, STS similarity | `samples`; baseline smoke and metric tests | Preserve all samples | None expected |
| EvalKit | Providers CLI exact fetch; cloud/local/cached results; Core envelope v3 parser | Authentication/no-fallback, hash/version tests | CLI catalog discovery, separate caches, governance gate | Authorization, stale catalog, exact matching |
| PSAPI | Four-model catalog; pair-only external EvalKit backend | Space v6 previously live-verified; opt-in test | Version/build registry and main-runner integration | Runtime mismatch, stale binary provenance |
| Multi-model evaluation | Core engine; App screen 03 | Bounded concurrency, cancellation, imports, metric tests | Add dataset runner without changing text runner contract | Partial results, persistence |
| Pair comparison | App screen 00; PairComparer | Dataset/version checks, rank metrics, unknown judgments, report tests | Build identity in selection and reproducibility | Comparing different datasets or raw scales |
| Threshold explorer | App screen 04; ThresholdEngine | Cached-score recalculation tests | Preserve | No model reinvocation |
| Comparator | App screen 05; reference changes, heatmap/radar/curves/distributions/errors/ranking | Hand-computed metric tests; desktop smoke | Include build provenance | Misleading performance claims |
| Scenarios/workbooks | `.smeval` format v1; WorkspaceService | Exact-identity, round-trip, missing-dependency tests | Optional metadata only; keep old readers' defaults | Backward compatibility, missing sources |
| Export | JSON/CSV/text/HTML; pair report schema 2.0, 28 sections | Encoding, redaction, sensitive opt-in tests | Apply governance before export | Sensitive content leakage |
| Privacy/settings | Credential Manager, NetworkPolicy, explicit consent; AppSettings JSON | Credential/redaction/offline tests | Public/internal cache segregation and governance | Silent approval, external transmission |
| Packaging | Existing `build\Publish.ps1` and `build\Smoke.ps1` | Prior self-contained build and UI smoke | Re-run after enhancements | Native dependencies, UI navigation |

## Verified discovery and unresolved gates

- WorkIQ: no connected tool or installed command in this session. No Teams, mail,
  SharePoint or WorkIQ search evidence is claimed.
- Authorized ASG-SearchPlatform checkout HEAD:
  `45af1ee4f2169d5c11f6900bab6f56fb31f078d0` (2026-11-05), `develop`.
  The backend's latest change remains
  `0527efc2519feabfdafbfeec6f1b4caccc97dd2d` (2026-10-08).
- Local refs: `develop`, `origin/develop`. These are **backend repository** refs,
  not proof of PSAPI release/development branch mapping.
- `ExternalBackends\psapi-embeddings-backend`: external protocol 2, Windows x64 CPU,
  `ps-*` package version `9.0.0.309937`. Package versions, input modalities and
  model-specific limits remain as documented in `PSAPI_DISCOVERY.md`.
- The authorized ADO repository inventory includes `PerceptiveShell.Incubation`,
  default `main`. Its name alone does not establish a deployed PSAPI contract.
- ADO work item 63647076 currently has title **Blocklist evaluation/update for Genoa**,
  state **Proposed**, changed 2026-11-03. No privacy/CELA approval fields were returned.
  This conflicts with the supplied reference title. Do not infer approvals or storage
  locations. The eight requested RAI families remain inventory labels, not registered
  identifiers. Resolve identifiers through EvalKit and approval evidence through owners.
- The setup guide `.docx` is still absent from the workspace. Technical references:
  EvalKit.Viewer `DatasetFetcher`, CLI `ListCommand`, `CatalogCommand`,
  `CacheCommand.Fetch`, and Shared manifest/source-record definitions.
- The CLI provides `list --all-versions` (text, no JSON option) and exact
  `cache fetch --dataset --version --expected-payload-hash`; do not invent commands.
- Manifest source records have licensing/approval-context/evidence fields, but free-text
  approval context is not a machine-verifiable privacy/CELA decision for a requested use.

## Architecture and UI plan

Keep all existing tabs and navigation indices. Append a Dataset Hub with All,
Hugging Face, Microsoft Internal and Local source filters; pinned repository metadata,
explicit file/column mapping, bounded acquisition and existing evaluation handoff.
Do not run repository scripts or use unpinned viewer rows as benchmark data.

Reuse EvalKit CLI discovery/fetch instead of implementing another internal downloader.
Keep existing cloud-batch/run selection and Get Matching Dataset workflow. Maintain exact
identity and separate public, internal non-sensitive, internal sensitive and result caches.
Approval/access are separate: successful storage authentication is not approval.

Add a PSAPI build registry with explicit inspected-contract compatibility, artifact
fingerprints, source/runtime distinction, and isolated external jobs. Preserve the legacy
auto-discovered backend and all existing provider settings. Add a whole-dataset runner
interface so PSAPI can participate in the existing multi-model runner, scenarios and
workbooks without pretending that it implements per-item embeddings.

## Public benchmark reconnaissance

Metadata queried directly from official Hugging Face Hub APIs on 2026-11-08.
License metadata is not a substitute for reviewing dataset/image usage terms.

| Benchmark | Verified repository / shape | Download eligibility |
|---|---|---|
| BEIR SciFact | `BeIR/scifact`, revision `b3b5335604bf5ee3c4447671af975ea25143d4f5`; corpus/queries Parquet; `BeIR/scifact-qrels`, revision `2938d17dc3b09882fdb8c12bbbe2e2dc0e75a029`; TSV | CC-BY-SA-4.0; small static files, suitable for explicit mapped import |
| MS MARCO | `microsoft/ms_marco`; nested passage Parquet | License absent from returned card metadata; no default download |
| MTEB STS | `mteb/stsbenchmark-sts` | License reported unknown; no default download |
| MIRACL | `miracl/miracl`; multilingual topic/qrel TSV | Apache-2.0 card; needs corpus and explicit schema mapping, not a single ready dataset |
| Flickr30k | `nlphuji/flickr30k`; annotations plus separate assets | License absent; not auto-approved/downloaded |
| COCO captions | `jxie/coco_captions`; image Parquet shards | License absent; not auto-approved/downloaded |
| WIT | `wikimedia/wit_base`; ~0.93 GB shards, nested multilingual metadata | CC-BY-SA-4.0 card; exceeds default small-download budget, needs explicit subset/schema selection |
