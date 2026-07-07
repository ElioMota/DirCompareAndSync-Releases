using DirCompareAndSync.Core.Model;
using DirCompareAndSync.Core.Storage;

namespace DirCompareAndSync.Core.Services;

public sealed class SessionService(JsonStorageService storage)
{
    private readonly JsonStorageService _storage = storage;

    public Task<IReadOnlyList<SyncJob>> ListAsync(string storageRoot) =>
        _storage.LoadJobsAsync(storageRoot);

    public async Task<SyncJob?> GetByNameAsync(string storageRoot, string name)
    {
        var jobs = await _storage.LoadJobsAsync(storageRoot);
        return jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SyncJob?> GetByIdAsync(string storageRoot, Guid id)
    {
        var jobs = await _storage.LoadJobsAsync(storageRoot);
        return jobs.FirstOrDefault(j => j.Id == id);
    }

    public SyncJob BuildJob(
        string name,
        string leftPath,
        string rightPath,
        SyncMode mode,
        bool useHash,
        SyncFilterRules? filterRules = null,
        IEnumerable<string>? legacyExcludeFilters = null) =>
        BuildJob(
            name,
            [new FolderPairConfig { LeftPath = leftPath, RightPath = rightPath }],
            mode,
            useHash,
            filterRules,
            legacyExcludeFilters);

    public SyncJob BuildJob(
        string name,
        IReadOnlyList<FolderPairConfig> folderPairs,
        SyncMode mode,
        bool useHash,
        SyncFilterRules? filterRules = null,
        IEnumerable<string>? legacyExcludeFilters = null,
        Guid? preserveId = null,
        int maxParallelFileCopies = SyncTransferSettings.DefaultMaxParallelFileCopies,
        JobEmailNotificationMode emailNotification = JobEmailNotificationMode.None,
        GridViewFilter? gridViewFilter = null)
    {
        var rules = filterRules?.Clone() ?? SyncFilterRules.CreateDefault();
        if (filterRules is null && legacyExcludeFilters is not null)
        {
            rules.ExcludePatterns = legacyExcludeFilters
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
        }

        rules.NormalizeLegacyFilters();

        var pairs = folderPairs
            .Select(ClonePairConfig)
            .Where(StorageJobHelper.IsPairConfigured)
            .ToList();

        if (pairs.Count == 0)
        {
            pairs.Add(new FolderPairConfig());
        }

        pairs[0].EnsureStorageLocations();
        var job = new SyncJob
        {
            Id = preserveId ?? Guid.Empty,
            Name = name.Trim(),
            LeftPath = pairs[0].Left.GetDisplayRoot(),
            RightPath = pairs[0].Right.GetDisplayRoot(),
            FolderPairs = pairs,
            Mode = mode,
            CompareMethod = useHash && !pairs.Any(p => p.Left.IsRemote || p.Right.IsRemote)
                ? CompareMethod.Hash
                : CompareMethod.TimeSize,
            FilterRules = rules,
            MaxParallelFileCopies = SyncTransferSettings.NormalizeJobValue(maxParallelFileCopies),
            EmailNotification = emailNotification,
            GridViewFilter = gridViewFilter
        };

        if (!preserveId.HasValue)
        {
            job.Id = JobIdHelper.CreateDeterministic(job);
        }

        return job;
    }

    private static FolderPairConfig ClonePairConfig(FolderPairConfig source)
    {
        source.EnsureStorageLocations();
        return new FolderPairConfig
        {
            Left = source.Left.Clone(),
            Right = source.Right.Clone(),
            Label = source.Label
        };
    }

    public async Task<SyncJob> SaveAsync(string storageRoot, SyncJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            throw new ArgumentException("O nome da sessão é obrigatório.", nameof(job));
        }

        job.Name = job.Name.Trim();
        job.EnsureFolderPairs();

        var jobs = (await _storage.LoadJobsAsync(storageRoot)).ToList();
        var byNameIndex = jobs.FindIndex(j =>
            string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));
        var byIdIndex = jobs.FindIndex(j => j.Id == job.Id);

        if (byNameIndex >= 0)
        {
            job.Id = jobs[byNameIndex].Id;
            jobs[byNameIndex] = job;
        }
        else if (byIdIndex >= 0)
        {
            jobs[byIdIndex] = job;
        }
        else
        {
            if (job.Id == Guid.Empty)
            {
                job.Id = JobIdHelper.CreateDeterministic(job);
            }

            jobs.Add(job);
        }

        await _storage.SaveJobsAsync(storageRoot, jobs);
        return job;
    }

    public async Task<bool> DeleteAsync(string storageRoot, string name)
    {
        var jobs = (await _storage.LoadJobsAsync(storageRoot)).ToList();
        var removed = jobs.RemoveAll(j =>
            string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            return false;
        }

        await _storage.SaveJobsAsync(storageRoot, jobs);
        return true;
    }

    public async Task<bool> DeleteByIdAsync(string storageRoot, Guid id)
    {
        var jobs = (await _storage.LoadJobsAsync(storageRoot)).ToList();
        var removed = jobs.RemoveAll(j => j.Id == id);

        if (removed == 0)
        {
            return false;
        }

        await _storage.SaveJobsAsync(storageRoot, jobs);
        return true;
    }

    public async Task TouchLastRunAsync(string storageRoot, SyncJob job)
    {
        var jobs = (await _storage.LoadJobsAsync(storageRoot)).ToList();
        var existing = job.Id != Guid.Empty
            ? jobs.FirstOrDefault(j => j.Id == job.Id)
            : jobs.FirstOrDefault(j =>
                string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            return;
        }

        job.Id = existing.Id;
        job.LastRun = DateTimeOffset.UtcNow;
        var index = jobs.FindIndex(j => j.Id == existing.Id);
        jobs[index] = job;
        await _storage.SaveJobsAsync(storageRoot, jobs);
    }

    public async Task<SyncJob?> RenameAsync(string storageRoot, Guid id, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("O nome da configuração é obrigatório.", nameof(newName));
        }

        var jobs = (await _storage.LoadJobsAsync(storageRoot)).ToList();
        var index = jobs.FindIndex(j => j.Id == id);
        if (index < 0)
        {
            return null;
        }

        if (jobs.Any(j => j.Id != id && string.Equals(j.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Já existe uma configuração com o nome «{newName}».");
        }

        jobs[index].Name = newName;
        await _storage.SaveJobsAsync(storageRoot, jobs);
        return jobs[index];
    }

    public Task<AppPreferences> LoadAppPreferencesAsync(string storageRoot) =>
        _storage.LoadAppPreferencesAsync(storageRoot);

    public async Task RememberLastUsedAsync(string storageRoot, SyncJob job)
    {
        if (job.Id == Guid.Empty && string.IsNullOrWhiteSpace(job.Name))
        {
            return;
        }

        var prefs = await LoadAppPreferencesAsync(storageRoot);
        prefs.LastUsedJobId = job.Id == Guid.Empty ? null : job.Id;
        prefs.LastUsedJobName = job.Name.Trim();
        await _storage.SaveAppPreferencesAsync(storageRoot, prefs);
    }

    public async Task MarkVersionSeenAsync(string storageRoot, string versionDisplay)
    {
        if (string.IsNullOrWhiteSpace(versionDisplay))
        {
            return;
        }

        var prefs = await LoadAppPreferencesAsync(storageRoot);
        prefs.LastSeenVersionDisplay = versionDisplay.Trim();
        await _storage.SaveAppPreferencesAsync(storageRoot, prefs);
    }

    public async Task ExportToFileAsync(string storageRoot, string filePath)
    {
        var jobs = await ListAsync(storageRoot);
        await _storage.SaveJobsToFileAsync(filePath, jobs);
    }

    public async Task ExportJobsToFileAsync(string filePath, IReadOnlyList<SyncJob> jobs)
    {
        await _storage.SaveJobsToFileAsync(filePath, jobs);
    }

    public async Task<SessionImportResult> ImportFromFileAsync(string storageRoot, string filePath, bool merge)
    {
        if (IsFreeFileSyncConfig(filePath))
        {
            return (await ImportFreeFileSyncAsync(storageRoot, filePath, merge)).SessionResult;
        }

        return merge
            ? await ImportMergeAsync(storageRoot, filePath)
            : await ImportReplaceAsync(storageRoot, filePath);
    }

    public async Task<FreeFileSyncImportResult> ImportFreeFileSyncAsync(
        string storageRoot,
        string filePath,
        bool merge)
    {
        var (job, warnings) = FreeFileSyncImporter.ImportFromFileDetailed(filePath);
        var sessionResult = merge
            ? await ImportJobMergeAsync(storageRoot, job)
            : await ImportJobReplaceAsync(storageRoot, job);

        return new FreeFileSyncImportResult(sessionResult, job.Name, warnings);
    }

    private static bool IsFreeFileSyncConfig(string filePath) =>
        SessionExportFormats.IsFreeFileSyncConfig(filePath);

    private async Task<SessionImportResult> ImportJobMergeAsync(string storageRoot, SyncJob job)
    {
        var existing = (await ListAsync(storageRoot)).ToList();
        var index = existing.FindIndex(j =>
            string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            job.Id = existing[index].Id;
            job.LastRun = existing[index].LastRun;
            existing[index] = job;
            await _storage.SaveJobsAsync(storageRoot, existing);
            return new SessionImportResult(0, 1, 1, ReplacedAll: false);
        }

        existing.Add(job);
        await _storage.SaveJobsAsync(storageRoot, existing);
        return new SessionImportResult(1, 0, 1, ReplacedAll: false);
    }

    private async Task<SessionImportResult> ImportJobReplaceAsync(string storageRoot, SyncJob job)
    {
        var existing = (await ListAsync(storageRoot)).ToList();
        var index = existing.FindIndex(j =>
            string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            job.Id = existing[index].Id;
            existing[index] = job;
            await _storage.SaveJobsAsync(storageRoot, existing);
            return new SessionImportResult(0, 1, 1, ReplacedAll: false);
        }

        existing.Add(job);
        await _storage.SaveJobsAsync(storageRoot, existing);
        return new SessionImportResult(1, 0, 1, ReplacedAll: false);
    }

    private async Task<SessionImportResult> ImportMergeAsync(string storageRoot, string filePath)
    {
        var imported = await _storage.LoadJobsFromFileAsync(filePath);
        var existing = (await ListAsync(storageRoot)).ToList();
        var added = 0;
        var updated = 0;

        foreach (var job in imported)
        {
            if (string.IsNullOrWhiteSpace(job.Name))
            {
                continue;
            }

            var index = existing.FindIndex(j =>
                string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                job.Id = existing[index].Id;
                job.LastRun = existing[index].LastRun;
                existing[index] = job;
                updated++;
            }
            else
            {
                existing.Add(job);
                added++;
            }
        }

        await _storage.SaveJobsAsync(storageRoot, existing);
        return new SessionImportResult(added, updated, imported.Count, ReplacedAll: false);
    }

    private async Task<SessionImportResult> ImportReplaceAsync(string storageRoot, string filePath)
    {
        var imported = await _storage.LoadJobsFromFileAsync(filePath);
        var existing = (await ListAsync(storageRoot)).ToList();
        var added = 0;
        var replaced = 0;

        foreach (var job in imported)
        {
            if (string.IsNullOrWhiteSpace(job.Name))
            {
                continue;
            }

            var index = existing.FindIndex(j =>
                string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                job.Id = existing[index].Id;
                existing[index] = job;
                replaced++;
            }
            else
            {
                existing.Add(job);
                added++;
            }
        }

        await _storage.SaveJobsAsync(storageRoot, existing);
        return new SessionImportResult(added, replaced, imported.Count, ReplacedAll: false);
    }
}
