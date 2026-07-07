using DirCompareAndSync.Core.Model;
using DirCompareAndSync.Core.Storage;

namespace DirCompareAndSync.Core.Services;

public sealed class SyncOrchestrator(
    StorageBackendFactory backendFactory,
    CompareEngine compareEngine,
    SyncPlanner planner,
    SyncExecutor executor,
    JsonStorageService storage)
{
    private readonly StorageBackendFactory _backendFactory = backendFactory;
    private readonly CompareEngine _compareEngine = compareEngine;
    private readonly SyncPlanner _planner = planner;
    private readonly SyncExecutor _executor = executor;
    private readonly JsonStorageService _storage = storage;

    public SyncJob CreateJob(
        string leftPath,
        string rightPath,
        SyncMode mode,
        bool useHash,
        SyncFilterRules? filterRules = null,
        IEnumerable<string>? legacyExcludeFilters = null)
    {
        var rules = filterRules?.Clone() ?? SyncFilterRules.CreateDefault();
        if (filterRules is null && legacyExcludeFilters is not null)
        {
            rules.ExcludePatterns = legacyExcludeFilters
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
        }

        rules.NormalizeLegacyFilters();

        var left = new StorageLocation { Kind = StorageKind.Local, Path = leftPath };
        var right = new StorageLocation { Kind = StorageKind.Local, Path = rightPath };
        left.EnsureLegacyPathFields();
        right.EnsureLegacyPathFields();

        return new SyncJob
        {
            Id = JobIdHelper.CreateDeterministic(left.GetDisplayRoot(), right.GetDisplayRoot(), mode),
            Name = $"Job_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}",
            LeftPath = left.GetDisplayRoot(),
            RightPath = right.GetDisplayRoot(),
            FolderPairs = [new FolderPairConfig { Left = left, Right = right }],
            Mode = mode,
            CompareMethod = useHash ? CompareMethod.Hash : CompareMethod.TimeSize,
            FilterRules = rules
        };
    }

    public async Task<CompareSession> CompareAsync(
        SyncJob job,
        string storageRoot,
        CancellationToken cancellationToken = default,
        Action<SyncProgressInfo>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        job.EnsureFilterRules();
        job.EnsureFolderPairs();

        var compareMethod = StorageJobHelper.GetEffectiveCompareMethod(job);
        var includeHash = compareMethod == CompareMethod.Hash;
        var history = await _storage.LoadHistoryAsync(storageRoot, job.Id);
        var allDiffs = new List<DiffResult>();
        var allPlan = new List<SyncOperation>();

        var validPairs = job.FolderPairs
            .Select((pair, index) => (pair, index))
            .Where(x => MultiPairCompareHelper.IsValidPair(x.pair))
            .ToList();

        var pairOrdinal = 0;
        foreach (var (pair, index) in validPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pairOrdinal++;

            var pairJob = job.CloneForPair(pair);
            var prefix = MultiPairCompareHelper.BuildPairPrefix(pair, index);
            var pairHistory = FilterHistoryForPair(history, prefix);
            var pairLabel = pair.GetDisplayLabel(index);

            await using var leftBackend = _backendFactory.Create(pair.Left);
            await using var rightBackend = _backendFactory.Create(pair.Right);

            var pairRules = job.FilterRules.GetEffectiveForPair(index);

            progress?.Invoke(new SyncProgressInfo(
                SyncProgressPhase.Compare,
                pairOrdinal,
                validPairs.Count,
                $"{pairLabel} — lado esquerdo"));

            var leftEntries = await ScanSideAsync(
                leftBackend, pairRules, includeHash, true, false, cancellationToken);

            progress?.Invoke(new SyncProgressInfo(
                SyncProgressPhase.Compare,
                pairOrdinal,
                validPairs.Count,
                $"{pairLabel} — lado direito"));

            var rightEntries = await ScanSideAsync(
                rightBackend, pairRules, includeHash, false, true, cancellationToken);

            var diffs = _compareEngine.Compare(leftEntries, rightEntries, pairHistory, compareMethod);
            var plan = _planner.BuildPlan(pairJob, diffs);

            allDiffs.AddRange(diffs.Select(d => MultiPairCompareHelper.WithPrefixedPath(d, prefix)));
            allPlan.AddRange(plan.Select(p => MultiPairCompareHelper.WithPrefixedPath(p, prefix, index)));
        }

        return new CompareSession(job, allDiffs, allPlan);
    }

    private static IReadOnlyDictionary<string, SyncHistoryEntry> FilterHistoryForPair(
        IReadOnlyDictionary<string, SyncHistoryEntry> history,
        string prefix)
    {
        var result = new Dictionary<string, SyncHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in history)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = key[prefix.Length..];
            result[relative] = entry;
        }

        return result;
    }

    public async Task ExecuteAsync(
        SyncJob job,
        IReadOnlyList<SyncOperation> plan,
        bool dryRun,
        bool confirmDelete,
        Func<SyncOperation, Task<bool>> confirmDeleteCallback,
        Action<string> log,
        string storageRoot,
        CancellationToken cancellationToken = default,
        SyncHistoryUpdateScope historyUpdateScope = SyncHistoryUpdateScope.FullTree,
        Action<SyncProgressInfo>? progress = null)
    {
        var executionCompleted = false;
        try
        {
            await _executor.ExecuteAsync(
                job,
                plan,
                _backendFactory,
                dryRun,
                confirmDelete,
                confirmDeleteCallback,
                log,
                progress,
                cancellationToken);
            executionCompleted = true;
        }
        finally
        {
            if (!dryRun && !executionCompleted)
            {
                await TryUpdateHistoryForCompletedOperationsAsync(job, plan, storageRoot, log);
            }
        }

        if (!dryRun && executionCompleted)
        {
            if (historyUpdateScope == SyncHistoryUpdateScope.ExecutedOperationsOnly)
            {
                await UpdateHistoryForExecutedOperationsAsync(job, plan, storageRoot, cancellationToken);
                log("Histórico actualizado (operações executadas).");
            }
            else
            {
                await UpdateHistoryFullTreeAsync(job, storageRoot, log, cancellationToken, progress);
            }
        }
    }

    private async Task TryUpdateHistoryForCompletedOperationsAsync(
        SyncJob job,
        IReadOnlyList<SyncOperation> plan,
        string storageRoot,
        Action<string> log)
    {
        if (!plan.Any(o => o.Status == OperationStatus.Completed))
        {
            return;
        }

        try
        {
            await UpdateHistoryForExecutedOperationsAsync(job, plan, storageRoot, CancellationToken.None);
            log("Histórico actualizado (operações concluídas antes da interrupção).");
        }
        catch (Exception ex)
        {
            log($"[AVISO] Não foi possível actualizar histórico: {ex.Message}");
        }
    }

    private async Task UpdateHistoryFullTreeAsync(
        SyncJob job,
        string storageRoot,
        Action<string> log,
        CancellationToken cancellationToken,
        Action<SyncProgressInfo>? progress = null)
    {
        job.EnsureFolderPairs();
        var compareMethod = StorageJobHelper.GetEffectiveCompareMethod(job);
        var includeHash = compareMethod == CompareMethod.Hash;
        var mergedHistory = new List<SyncHistoryEntry>();

        var validPairs = job.FolderPairs
            .Select((pair, index) => (pair, index))
            .Where(x => MultiPairCompareHelper.IsValidPair(x.pair))
            .ToList();

        var pairOrdinal = 0;
        foreach (var (pair, index) in validPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pairOrdinal++;

            var pairJob = job.CloneForPair(pair);
            var prefix = MultiPairCompareHelper.BuildPairPrefix(pair, index);
            var pairLabel = pair.GetDisplayLabel(index);

            await using var leftBackend = _backendFactory.Create(pair.Left);
            await using var rightBackend = _backendFactory.Create(pair.Right);

            var pairRules = job.FilterRules.GetEffectiveForPair(index);

            progress?.Invoke(new SyncProgressInfo(
                SyncProgressPhase.Compare,
                pairOrdinal,
                validPairs.Count,
                $"{pairLabel} — histórico (esquerda)"));

            var leftAfter = await ScanSideAsync(
                leftBackend, pairRules, includeHash, true, false, cancellationToken);

            progress?.Invoke(new SyncProgressInfo(
                SyncProgressPhase.Compare,
                pairOrdinal,
                validPairs.Count,
                $"{pairLabel} — histórico (direita)"));

            var rightAfter = await ScanSideAsync(
                rightBackend, pairRules, includeHash, false, true, cancellationToken);
            var pairHistory = BuildHistory(pairJob, leftAfter, rightAfter);

            foreach (var entry in pairHistory)
            {
                entry.RelativePath = prefix + entry.RelativePath;
                mergedHistory.Add(entry);
            }
        }

        await _storage.SaveHistoryAsync(storageRoot, job.Id, mergedHistory);
        log("Histórico atualizado.");
    }

    private async Task UpdateHistoryForExecutedOperationsAsync(
        SyncJob job,
        IReadOnlyList<SyncOperation> plan,
        string storageRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await _storage.LoadHistoryAsync(storageRoot, job.Id);
        var byPath = new Dictionary<string, SyncHistoryEntry>(existing, StringComparer.OrdinalIgnoreCase);

        foreach (var operation in plan)
        {
            if (operation.Status != OperationStatus.Completed)
            {
                continue;
            }

            var entry = byPath.GetValueOrDefault(operation.RelativePath)
                        ?? new SyncHistoryEntry
                        {
                            JobId = job.Id,
                            RelativePath = operation.RelativePath
                        };

            SyncHistorySnapshotHelper.ApplyCompletedOperation(entry, operation, job);
            byPath[operation.RelativePath] = entry;
        }

        await _storage.SaveHistoryAsync(storageRoot, job.Id, byPath.Values.ToList());
    }

    private static async Task<IReadOnlyDictionary<string, FileEntry>> ScanSideAsync(
        IStorageBackend backend,
        SyncFilterRules filterRules,
        bool includeHash,
        bool existsOnLeft,
        bool existsOnRight,
        CancellationToken cancellationToken)
    {
        var entries = await backend.ScanAsync(filterRules, includeHash, cancellationToken);
        foreach (var entry in entries.Values)
        {
            if (existsOnLeft)
            {
                entry.ExistsOnLeft = true;
            }

            if (existsOnRight)
            {
                entry.ExistsOnRight = true;
            }
        }

        return entries;
    }

    private static List<SyncHistoryEntry> BuildHistory(
        SyncJob job,
        IReadOnlyDictionary<string, FileEntry> leftEntries,
        IReadOnlyDictionary<string, FileEntry> rightEntries)
    {
        var all = leftEntries.Keys.Union(rightEntries.Keys, StringComparer.OrdinalIgnoreCase);
        var list = new List<SyncHistoryEntry>();

        foreach (var path in all)
        {
            leftEntries.TryGetValue(path, out var left);
            rightEntries.TryGetValue(path, out var right);
            list.Add(new SyncHistoryEntry
            {
                JobId = job.Id,
                RelativePath = path,
                LastLeftModifiedUtc = left?.LastModifiedUtc,
                LastRightModifiedUtc = right?.LastModifiedUtc,
                LastHash = left?.Hash ?? right?.Hash
            });
        }

        return list;
    }
}

public sealed record CompareSession(
    SyncJob Job,
    IReadOnlyList<DiffResult> Diffs,
    IReadOnlyList<SyncOperation> Plan);
