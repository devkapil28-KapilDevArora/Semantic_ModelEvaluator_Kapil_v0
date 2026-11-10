# PSAPI and Hugging Face semantic-search discovery note

This note records where the integration contracts for the pair-comparison models come from, before any adapter code relied on them.
It contains no secrets, tokens, or endpoint values.

## How discovery was done

| Source | Result |
|---|---|
| WorkIQ | **Unavailable in this session.** No WorkIQ query could be run, so no owner, wiki, or pull-request evidence from WorkIQ is used here. |
| Authorized local checkout of ASG-SearchPlatform | **Used.** This was the primary evidence. The user's authenticated clone was at `%USERPROFILE%\source\ASG-SearchPlatform`. |
| Active build | **Used.** The backend was built from that source and run end to end through the EvalKit CLI (see *Validation*). |
| EvalKit Threshold Explorer setup guide (.docx) | **Not available.** It was not found on disk. The EvalKit dataset and result contracts come from the EvalKit source in the same checkout (EvalKit.CLI, EvalKit.Viewer, AsgSearchPlatform.Manifest). |

The PSAPI source is authoritative by the validation rules for three reasons:

- Its README names all four models and the EvalKit integration.
- It contains the deployed request/response contract: the JSON-RPC backend and `backend.manifest.json`.
- It is the backend that EvalKit actually launches.

## Authoritative source (all four PSAPI models)

| Field | Value |
|---|---|
| Repository | ASG-SearchPlatform — `https://dev.azure.com/microsoft/OS.Developer/_git/ASG-SearchPlatform` |
| Azure DevOps org / project | microsoft / OS.Developer |
| Branch | `develop` (the default branch; there are no model-specific branches — all four models are selected by the `modelId` parameter) |
| Commit inspected | `0527efc2519feabfdafbfeec6f1b4caccc97dd2d` (2026-10-08) |
| Client project | `ExternalBackends/psapi-embeddings-backend` (C++ executable `EvalKit.Backend.Psapi.exe`) |
| Contract paths | `README.md`, `PsApiModels.cpp`, `Model.cpp`, `backend.manifest.json`, `packages.config` |
| Protocol | EvalKit external backend, JSON-RPC over stdio, manifest `protocolVersion` 2 |
| Authentication | **None at run time.** The backend loads local, encrypted PerceptiveShell packages on CPU, with no endpoint and no network call. Credentials are needed only at **build** time, to restore packages from the `ASG-SearchPlatform-Input` Azure Artifacts feed with the user's own sign-in. |
| Backend parameters | `modelId` (required), `textSource`, `maxResults`, `onQueryError`, `smartIndexing`. The backend rejects unknown parameters with JSON-RPC error -32602. The app sends only `modelId`, plus `textSource` when it is not the default. The result depth goes in the job's `queryConfig.maxResultsPerQuery` (1–10000). |
| Scoring | Cosine similarity of L2-normalized embeddings, mapped to (cos + 1) / 2 and clamped to [0, 1]. Higher is more similar. |
| Shared packages | `ps-*` 9.0.0.309937; `Microsoft.Windows.AI.MachineLearning` 2.5.77-rc |
| Owner | Not documented (no CODEOWNERS). The only git author on the path is John Senneker. |

## Per-model table

| Display name | Provider | modelId | Input type | Output type | Dimensions | Query limit | Model package (version) | Config keys | Integration status |
|---|---|---|---|---|---|---|---|---|---|
| Space v6 | PSAPI | `spacev6` | text query → **text** corpus | embedding score in [0, 1] | 100 | 512 tokens | EmbeddingsLookup-SpaceV6 (1.0.493-main-283748) | `modelId`, `textSource` (`inlineText` default \| `filename`) | **Live-validated** through the EvalKit CLI on CPU. Automated opt-in test: `SMEVAL_VERIFY_PSAPI=1`. |
| Palermo | PSAPI | `palermo` | text query → **text** corpus | embedding score in [0, 1] | 768 | 512 tokens | palermo_transformer-onnx-all-onnx-1.17-default-int-encrypted (1.0.538-main) | `modelId`, `textSource` | Implemented; uses the same path as Space v6. The package is marked **experimental** upstream. |
| Florence | PSAPI | `florence` | text query → **image** corpus (image file paths) | embedding score in [0, 1] | 768 | 77 tokens | EmbeddingsLookup-FlorenceCompliantD3 (1.0.128-main-181999) | `modelId` | Implemented. Needs an EvalKit image dataset; text datasets are blocked before the run. |
| Genoa | PSAPI | `genoa` | text query → **image** corpus (image file paths) | embedding score in [0, 1] | 1024 | 96 tokens | EmbeddingsLookup-Genoa (1.0.521-main-296876) | `modelId` | Implemented. Needs an EvalKit image dataset; text datasets are blocked before the run. |

### Behavioral differences

- **Corpus modality.** Space v6 and Palermo compare text with text. Florence and Genoa compare text with images.
  - The app blocks a run when the dataset modality does not match the model (`PsapiJob.CheckDataset`).
  - A comparison across modalities is reported as not like-for-like.
- **Truncation.** Query token budgets differ (512, 512, 77 and 96 tokens). The compatibility check reports this, because long queries are truncated differently.
- **Score scale.** All four models share the same [0, 1] mapping. Even so, the app compares them on **rank- and judgment-based** metrics and does not treat raw scores as calibrated across models.

Registered-build validation records the build identity, runtime version, artifact fingerprint, and model-specific issues.
A backend-wide contract error makes the status non-runnable and is surfaced in its summary, even when the CLI and backend paths exist.

## Validation performed

1. Built the backend from the inspected commit with `build.ps1 -Configuration Release`. Visual Studio 2026 needs `-p:PlatformToolset=v145` because v143 is not installed.
2. Ran `evalkit run <job.json> --output-dir DIR --force` from the repository root, with `EVALKIT_HEADLESS=1`.
3. Confirmed that the run reported its identity: `evalkit.effective_config.parameters.model_id` = `spacev6`, dimension 100, device CPU.
4. Confirmed that every returned score lies in [0, 1].
5. Found and fixed a contract mismatch:
   - The authoritative judgment loader (`AsgSearchPlatform.Manifest.QueryJudgementLoader`) requires `doc_id`.
   - The app's reader previously accepted only `document_id`.
   - It now reads `doc_id` and falls back to `document_id`.
   - Query-result rows correctly use `document_id`.

## Hugging Face semantic-search catalog

| Field | Value |
|---|---|
| Existing integration | Reused (`HuggingFaceProvider`): ONNX download with a pinned revision, local CPU execution, and explicit pooling and normalization taken from `1_Pooling/config.json` |
| Default allowlist | `sentence-transformers/all-MiniLM-L6-v2`, `BAAI/bge-small-en-v1.5`, `intfloat/e5-small-v2`, `sentence-transformers/paraphrase-MiniLM-L3-v2` |
| Selector rule | `HuggingFaceProvider.IsSemanticSearchModel`: the model must support retrieval, must not be generative, and must produce embeddings. Text-generation, classification and reranking models are hidden from the pair selector. |
| Token | Optional; stored only in the Windows credential store, never in settings or reports |
| Startup | No model is downloaded at startup. Downloading starts only after an explicit user action and the network-consent check. |

## Open questions

| # | Question | Impact |
|---|---|---|
| 1 | Who owns `psapi-embeddings-backend`? There is no CODEOWNERS file, and WorkIQ was unavailable. | Escalation contact for contract changes |
| 2 | Are there reuse or licensing terms for the PerceptiveShell packages beyond internal evaluation? | The app never copies or redistributes them; it only launches the locally built backend. |
| 3 | Palermo is marked as an experimental package. Is a stable package planned? | Palermo results may change between package versions; the package version is recorded as the model revision. |
| 4 | The v143 toolset is missing on VS 2026 build machines. | Build workaround: `-p:PlatformToolset=v145`. |
| 5 | Is there a remote/hosted PSAPI endpoint? None was found in the inspected source. | The app does not implement or guess one. |
