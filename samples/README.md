# Sample datasets (synthetic)

All samples are hand-written or generated for demonstrating Semantic_ModelEvaluator_Kapil_V0. They are **synthetic** and
must not be used to claim real-world model quality.

| Folder | Task | Manifest | Notes |
|---|---|---|---|
| retrieval-it-helpdesk | Retrieval | it-helpdesk.smds.json | 40 docs, 12 queries (q12 unanswerable), graded 0-3 relevance, harm labels on unsafe advice; `predictions-bm25.csv` is an importable BM25 run |
| classification-safety | Safety (binary classification) | safety-requests.smds.json | 30 requests; two imported classifier outputs (B is missing e30 on purpose) |
| similarity-sts | Semantic similarity | sts.smds.json | 20 pairs with 0-5 gold similarity |
