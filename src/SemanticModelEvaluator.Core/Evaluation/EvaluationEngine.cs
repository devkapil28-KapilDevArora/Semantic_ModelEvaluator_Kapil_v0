using System.Diagnostics;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Providers;

namespace SemanticModelEvaluator.Core.Evaluation;

public sealed record EvaluationProgress(
    string Stage,
    string ModelName,
    int ModelIndex,
    int ModelCount,
    int Completed,
    int Total,
    TimeSpan Elapsed,
    int FailedItems,
    string? Warning = null)
{
    public int Remaining => Math.Max(0, Total - Completed);
}

public sealed record ModelPlan(ModelDescriptor Model, string? PredictionsPath = null);

public sealed class CompatibilityReport
{
    public List<(string Model, ValidationSeverity Severity, string Message)> Items { get; } = [];
    /// <summary>The run can start when the dataset is valid and at least one model is runnable; non-runnable models are skipped.</summary>
    public bool CanRun => HasRunnableModel && !Items.Any(i => i.Model == "Dataset" && i.Severity == ValidationSeverity.Error);
    public bool HasRunnableModel { get; set; }
}

public interface IRunStore
{
    Task SaveAsync(EvaluationRun run, CancellationToken ct);
}

public sealed class FileRunStore(string directory) : IRunStore
{
    public Task SaveAsync(EvaluationRun run, CancellationToken ct) =>
        AtomicFile.WriteAllTextAsync(Path.Combine(directory, run.RunId + ".json"),
            System.Text.Json.JsonSerializer.Serialize(run, Persistence.WorkspaceSerializer.Options), CancellationToken.None);
}

public sealed class EvaluationEngine(ProviderRegistry providers, EmbeddingCache? cache = null, IRunStore? store = null)
{
    /// <summary>Informational only; does not mark a run as completed with errors.</summary>
    public const string ConcurrencyNote = "Queries ran concurrently; per-query latency includes contention.";
    public const int MaxConsecutiveItemFailures = 5;

    /// <summary>
    /// True when a batch failure may be caused by one input (HTTP 400/413/422, malformed response, tokenizer error), so the batch is split to isolate it
    /// (bounded by <see cref="MaxConsecutiveItemFailures"/>). Throttling/timeouts/5xx were already retried by the provider and auth/config/unsupported
    /// errors are systematic, so those are never re-sent item by item.
    /// </summary>
    public static bool IsItemSpecific(Exception ex) => ex switch
    {
        NotSupportedException => false,
        HttpRequestException { StatusCode: { } code } => (int)code is 400 or 413 or 422,
        HttpRequestException => false,
        _ => true,
    };

    public static CompatibilityReport CheckCompatibility(IReadOnlyList<ModelPlan> plans, EvalDataset dataset, EvalTask task)
    {
        var report = new CompatibilityReport();
        if (dataset.ValidationIssues.Any(i => i.Severity == ValidationSeverity.Error))
            report.Items.Add(("Dataset", ValidationSeverity.Error, "Dataset has validation errors: " + dataset.ValidationIssues.First(i => i.Severity == ValidationSeverity.Error).Message));
        if (task != dataset.Task)
            report.Items.Add(("Dataset", ValidationSeverity.Error, $"Dataset task is {dataset.Task}; cannot evaluate as {task}."));
        foreach (var p in plans)
        {
            var m = p.Model;
            var name = m.DisplayName;
            if (!m.Supports(task)) { report.Items.Add((name, ValidationSeverity.Error, $"Model does not support {task}.")); continue; }
            if (m.Execution == ExecutionKind.ImportedPredictions)
            {
                if (p.PredictionsPath is null && m.LocalPath is null) report.Items.Add((name, ValidationSeverity.Error, "No predictions file."));
                else report.Items.Add((name, ValidationSeverity.Info, "Uses imported predictions; performance is not measured."));
                continue;
            }
            if (m.Readiness != ReadinessState.Ready) { report.Items.Add((name, ValidationSeverity.Error, $"Not ready: {m.Readiness} — {m.ReadinessDetail}")); continue; }
            bool datasetRunner = m.Properties.GetValueOrDefault("dataset_execution") == "true";
            bool native = datasetRunner || task switch
            {
                EvalTask.Retrieval or EvalTask.SemanticSimilarity => m.InputModality is Modality.Text or Modality.Multimodal && m.OutputModality == Modality.Embedding,
                _ => false
            };
            if (!native) { report.Items.Add((name, ValidationSeverity.Error, $"{task} cannot be executed natively with this model in V0; import predictions instead.")); continue; }
            if (datasetRunner && m.Properties.GetValueOrDefault("query_modality") == "text" && dataset.Queries.Any(q => q.ImagePath is not null || string.IsNullOrWhiteSpace(q.Text)))
            { report.Items.Add((name, ValidationSeverity.Error, "This backend requires text-only queries; image queries need imported predictions or another runner.")); continue; }
            if (datasetRunner && m.Properties.GetValueOrDefault("corpus_modality") == "image" && dataset.Documents.Any(d => d.ImagePath is null))
            { report.Items.Add((name, ValidationSeverity.Error, "This backend requires an image corpus for every candidate.")); continue; }
            if (datasetRunner && m.Properties.GetValueOrDefault("corpus_modality") == "text" && dataset.Documents.Any(d => d.ImagePath is not null))
            { report.Items.Add((name, ValidationSeverity.Error, "This backend requires a text corpus for every candidate.")); continue; }
            if (task == EvalTask.Retrieval && dataset.Documents.Any(d => string.IsNullOrEmpty(d.Text) && d.ImagePath is not null) && m.InputModality == Modality.Text)
                report.Items.Add((name, ValidationSeverity.Warning, "Image-only documents cannot be encoded by a text model and will be skipped."));
            if (m.IsSynthetic) report.Items.Add((name, ValidationSeverity.Warning, "Synthetic deterministic baseline — results are not representative of neural model quality."));
            report.Items.Add((name, ValidationSeverity.Info, $"{m.Identity.Key} · {m.RuntimeRequirements}"));
        }
        report.HasRunnableModel = plans.Any(p => !report.Items.Any(i => i.Model == p.Model.DisplayName && i.Severity == ValidationSeverity.Error));
        return report;
    }

    /// <summary>
    /// Runs every model. A failure or timeout in one model is recorded on that model and the run continues;
    /// the run is persisted after each model so completed work is never lost.
    /// </summary>
    public async Task<EvaluationRun> RunAsync(EvalDataset dataset, IReadOnlyList<ModelPlan> plans, EvaluationConfig config,
        IProgress<EvaluationProgress>? progress, CancellationToken ct, string? name = null)
    {
        DatasetPolicy.Ensure(dataset, DatasetOperation.Evaluate);
        if (plans.Any(p => p.Model.Properties.GetValueOrDefault("remote_inference") == "true"))
            DatasetPolicy.Ensure(dataset, DatasetOperation.ExternalProcessing);
        if (plans.Count == 0) throw new ArgumentException("Select at least one model.");
        if (config.K < 1 || config.RetainDepth < config.K || config.BatchSize < 1 || config.MaxConcurrency < 1)
            throw new ArgumentException("K, batch size and concurrency must be positive; retain depth must be at least K.");
        if (!string.Equals(config.Device, "CPU", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This version supports CPU execution only.");
        if (config.TimeoutSeconds < 1) throw new ArgumentException("Timeout must be positive.");
        var runStore = string.IsNullOrWhiteSpace(config.OutputDirectory) ? store : new FileRunStore(Path.GetFullPath(config.OutputDirectory));
        var run = new EvaluationRun { Dataset = dataset.ToReference(), Task = dataset.Task, Config = config, Name = name ?? dataset.Name, Status = RunStatus.Running };
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            var result = new ModelRunResult
            {
                Model = plan.Model.Identity,
                DisplayName = plan.Model.DisplayName,
                IsSynthetic = plan.Model.IsSynthetic,
                Execution = plan.Model.Execution,
                Status = RunStatus.Running
            };
            run.Models.Add(result);
            if (ct.IsCancellationRequested) { result.Status = RunStatus.Cancelled; continue; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds)));
            void Report(string stage, int done, int total, string? warn = null) =>
                progress?.Report(new(stage, plan.Model.DisplayName, i + 1, plans.Count, done, total, clock.Elapsed, result.FailedItems, warn));
            try
            {
                var compatibility = CheckCompatibility([plan], dataset, dataset.Task);
                if (!compatibility.CanRun)
                    throw new InvalidOperationException(string.Join(" ", compatibility.Items.Where(x => x.Severity == ValidationSeverity.Error).Select(x => x.Message)));
                await RunModelAsync(dataset, plan, config, result, Report, timeout.Token).ConfigureAwait(false);
                result.Status = result.FailedItems > 0 || result.Warnings.Any(w => w != ConcurrencyNote) ? RunStatus.CompletedWithErrors : RunStatus.Completed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result.Status = RunStatus.Cancelled;
                result.Error = "Cancelled by user.";
            }
            catch (OperationCanceledException)
            {
                result.Status = RunStatus.Failed;
                result.Error = $"Timed out after {config.TimeoutSeconds} s.";
            }
            catch (Exception ex)
            {
                result.Status = RunStatus.Failed;
                result.Error = Security.Redactor.Redact(ex.Message);
            }
            Report(result.Status.ToString(), 1, 1, result.Error);
            if (runStore is not null) await runStore.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        run.CompletedAt = DateTimeOffset.Now;
        run.Status = ct.IsCancellationRequested ? RunStatus.Cancelled
            : run.Models.All(m => m.Status == RunStatus.Completed) ? RunStatus.Completed
            : run.Models.Any(m => m.Status is RunStatus.Completed or RunStatus.CompletedWithErrors) ? RunStatus.CompletedWithErrors
            : RunStatus.Failed;
        if (runStore is not null) await runStore.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
        return run;
    }

    private async Task RunModelAsync(EvalDataset dataset, ModelPlan plan, EvaluationConfig config, ModelRunResult result,
        Action<string, int, int, string?> report, CancellationToken ct)
    {
        var model = plan.Model;
        if (model.Execution == ExecutionKind.ImportedPredictions)
        {
            report("Importing predictions", 0, 1, null);
            var path = plan.PredictionsPath ?? model.LocalPath!;
            if (model.Identity.ContentHash is { } expected)
            {
                using var input = File.OpenRead(path);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
                if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Predictions changed since registration. Register the new version before evaluating.");
            }
            var imported = await Task.Run(() => PredictionImporter.Import(path, model.Identity, model.DisplayName, dataset, config.RetainDepth), ct).ConfigureAwait(false);
            result.Queries = imported.Queries;
            result.Examples = imported.Examples;
            result.Warnings.AddRange(imported.Warnings);
            result.FailedItems = imported.FailedItems;
            result.Performance = imported.Performance;
            return;
        }

        report("Loading model", 0, 1, null);
        var provider = providers.Get(model.Identity.ProviderId);
        var load = Stopwatch.StartNew();
        await using var runner = await provider.LoadAsync(model, new RuntimeOptions(config.Device, BatchSize: config.BatchSize), ct).ConfigureAwait(false);
        load.Stop();
        if (runner is IDatasetModelRunner whole)
        {
            await whole.EvaluateAsync(dataset, config, result,
                new InlineProgress(p => report(p.Message, p.Fraction is { } fraction ? (int)(fraction * 100) : 0,
                    p.Fraction.HasValue ? 100 : 0, null)), ct).ConfigureAwait(false);
            return;
        }
        if (runner is not ITextEmbeddingRunner embedder)
            throw new NotSupportedException($"{model.DisplayName} does not provide text embeddings.");
        result.Performance = new PerformanceStats
        {
            Measured = true,
            LoadMs = runner.Runtime.LoadMilliseconds > 0 ? runner.Runtime.LoadMilliseconds : load.Elapsed.TotalMilliseconds,
            Device = runner.Runtime.Device,
            Runtime = runner.Runtime.RuntimeName
        };

        try
        {
            switch (dataset.Task)
            {
                case EvalTask.Retrieval:
                    await RunRetrievalAsync(dataset, config, result, embedder, report, ct).ConfigureAwait(false);
                    break;
                case EvalTask.SemanticSimilarity:
                    await RunSimilarityAsync(dataset, config, result, embedder, report, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"{dataset.Task} requires imported predictions in V0.");
            }
        }
        finally
        {
            if (runner is IUsageReportingRunner usage) result.Performance.Usage = usage.Usage;
        }
        result.Performance.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
    }

    private sealed class InlineProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => report(value);
    }

    private async Task<Dictionary<string, float[]>> EncodeAsync(ITextEmbeddingRunner embedder, IReadOnlyList<(string Id, string Text)> items,
        EmbeddingRole role, EvaluationConfig config, ModelRunResult result, Action<string, int, int, string?> report, string stage, CancellationToken ct)
    {
        string key = result.Model.Key + ":" + result.Model.ContentHash;
        var cached = config.UseEmbeddingCache && cache is not null ? cache.Load(key, role.ToString()) : new(StringComparer.Ordinal);
        var output = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var todo = new List<(string Id, string Text, string Hash)>();
        foreach (var (id, text) in items)
        {
            var h = EmbeddingCache.Hash(text);
            if (cached.TryGetValue(h, out var v)) output[id] = v; else todo.Add((id, text, h));
        }
        int done = items.Count - todo.Count;
        report(stage, done, items.Count, null);
        int batch = Math.Max(1, config.BatchSize);
        for (int i = 0; i < todo.Count; i += batch)
        {
            ct.ThrowIfCancellationRequested();
            var slice = todo.Skip(i).Take(batch).ToList();
            float[][] vectors;
            try { vectors = await embedder.EmbedAsync(slice.Select(s => s.Text).ToList(), role, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException and not AccessDeniedException && IsItemSpecific(ex))
            {
                // Retry individually so one bad item does not fail the batch; stop if failures are clearly not item-specific.
                vectors = new float[slice.Count][];
                int consecutive = 0;
                for (int j = 0; j < slice.Count; j++)
                {
                    try { vectors[j] = (await embedder.EmbedAsync([slice[j].Text], role, ct).ConfigureAwait(false))[0]; consecutive = 0; }
                    catch (Exception inner) when (inner is not OperationCanceledException and not AccessDeniedException && IsItemSpecific(inner))
                    {
                        vectors[j] = null!; result.FailedItems++; result.Warnings.Add($"{role} '{slice[j].Id}' failed: {Security.Redactor.Redact(inner.Message)}");
                        if (++consecutive >= MaxConsecutiveItemFailures)
                            throw new InvalidOperationException($"Stopped after {consecutive} consecutive item failures; the provider is failing systematically. Last error: {Security.Redactor.Redact(inner.Message)}", inner);
                    }
                }
            }
            for (int j = 0; j < slice.Count; j++)
                if (vectors[j] is { } v) { output[slice[j].Id] = v; cached[slice[j].Hash] = v; }
            done += slice.Count;
            report(stage, done, items.Count, null);
        }
        if (todo.Count > 0 && config.UseEmbeddingCache && cache is not null) cache.Save(key, role.ToString(), cached);
        return output;
    }

    private async Task RunRetrievalAsync(EvalDataset dataset, EvaluationConfig config, ModelRunResult result, ITextEmbeddingRunner embedder,
        Action<string, int, int, string?> report, CancellationToken ct)
    {
        var docs = dataset.Documents.Where(d => !string.IsNullOrEmpty(d.Text)).Select(d => (d.Id, d.Text)).ToList();
        int skipped = dataset.Documents.Count - docs.Count;
        if (skipped > 0) result.Warnings.Add($"{skipped} documents without text were not encoded.");
        var docClock = Stopwatch.StartNew();
        var docVectors = await EncodeAsync(embedder, docs, EmbeddingRole.Document, config, result, report, "Encoding documents", ct).ConfigureAwait(false);
        if (docVectors.Count == 0) throw new InvalidOperationException("No document could be encoded. " + string.Join(" ", result.Warnings));
        docClock.Stop();
        result.Performance.DocumentEncodeMs = docClock.Elapsed.TotalMilliseconds;
        if (docs.Count > 0) result.Performance.ThroughputPerSecond = docs.Count / Math.Max(1e-6, docClock.Elapsed.TotalSeconds);

        var ids = docVectors.Keys.ToArray();
        var matrix = ids.Select(id => Normalize(docVectors[id])).ToArray();
        var latencies = new List<double>();
        int done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrency));
        var results = new QueryResult[dataset.Queries.Count];
        var tasks = dataset.Queries.Select(async (q, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(q.Text)) { results[index] = new QueryResult { QueryId = q.Id, Error = "Query has no text." }; return; }
                var sw = Stopwatch.StartNew();
                float[] qv;
                try { qv = Normalize((await embedder.EmbedAsync([q.Text], EmbeddingRole.Query, ct).ConfigureAwait(false))[0]); }
                catch (Exception ex) when (ex is not OperationCanceledException and not AccessDeniedException)
                {
                    results[index] = new QueryResult { QueryId = q.Id, Error = Security.Redactor.Redact(ex.Message) };
                    return;
                }
                var top = TopK(qv, matrix, ids, config.RetainDepth);
                sw.Stop();
                results[index] = new QueryResult { QueryId = q.Id, Results = top, LatencyMs = sw.Elapsed.TotalMilliseconds };
                lock (latencies) latencies.Add(sw.Elapsed.TotalMilliseconds);
            }
            finally
            {
                gate.Release();
                report("Scoring queries", Interlocked.Increment(ref done), dataset.Queries.Count, null);
            }
        }).ToList();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally { result.Queries = [.. results.Where(r => r is not null)]; }
        int failed = results.Count(r => r.Error is not null);
        result.FailedItems += failed;
        if (failed > 0) result.Warnings.Add($"{failed} queries failed.");
        if (config.MaxConcurrency > 1) result.Warnings.Add(ConcurrencyNote);
        SetLatency(result.Performance, latencies);
    }

    private async Task RunSimilarityAsync(EvalDataset dataset, EvaluationConfig config, ModelRunResult result, ITextEmbeddingRunner embedder,
        Action<string, int, int, string?> report, CancellationToken ct)
    {
        var texts = dataset.Examples.SelectMany(e => new[] { e.Text, e.Text2 ?? "" }).Where(t => t.Length > 0).Distinct().Select(t => (Id: t, Text: t)).ToList();
        var clock = Stopwatch.StartNew();
        var vectors = await EncodeAsync(embedder, texts, EmbeddingRole.Document, config, result, report, "Encoding pairs", ct).ConfigureAwait(false);
        clock.Stop();
        foreach (var e in dataset.Examples)
        {
            if (e.Text2 is null || !vectors.TryGetValue(e.Text, out var a) || !vectors.TryGetValue(e.Text2, out var b))
            {
                result.Examples.Add(new ExampleResult { ExampleId = e.Id, Error = "Pair could not be encoded." });
                result.FailedItems++;
                continue;
            }
            result.Examples.Add(new ExampleResult { ExampleId = e.Id, Score = Dot(Normalize(a), Normalize(b)) });
        }
        result.Performance.ThroughputPerSecond = texts.Count / Math.Max(1e-6, clock.Elapsed.TotalSeconds);
        result.Performance.MeanLatencyMs = texts.Count == 0 ? null : clock.Elapsed.TotalMilliseconds / texts.Count * 2;
    }

    private static void SetLatency(PerformanceStats perf, List<double> latencies)
    {
        if (latencies.Count == 0) return;
        latencies.Sort();
        perf.MeanLatencyMs = latencies.Average();
        perf.P50LatencyMs = Percentile(latencies, 0.5);
        perf.P95LatencyMs = Percentile(latencies, 0.95);
    }

    internal static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        double pos = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public static float[] Normalize(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += x * x;
        n = Math.Sqrt(n);
        if (n == 0) return v;
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (float)(v[i] / n);
        return r;
    }

    public static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new InvalidOperationException("Embedding dimensions differ.");
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Exact cosine top-N (vectors pre-normalised). Ties broken by document id for determinism.</summary>
    public static List<ScoredDocument> TopK(float[] query, float[][] matrix, string[] ids, int k)
    {
        var scored = new ScoredDocument[matrix.Length];
        for (int i = 0; i < matrix.Length; i++) scored[i] = new ScoredDocument(ids[i], Math.Round(Dot(query, matrix[i]), 6));
        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.DocumentId, StringComparer.Ordinal).Take(k)];
    }
}
