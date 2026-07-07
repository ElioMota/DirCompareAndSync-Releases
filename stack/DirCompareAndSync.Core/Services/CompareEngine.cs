using DirCompareAndSync.Core.Model;

namespace DirCompareAndSync.Core.Services;

public sealed class CompareEngine
{
    /// <summary>Tolerância para comparação por data (pastas de rede / FAT).</summary>
    private static readonly TimeSpan TimeComparisonTolerance = TimeSpan.FromSeconds(2);

    public IReadOnlyList<DiffResult> Compare(
        IReadOnlyDictionary<string, FileEntry> leftEntries,
        IReadOnlyDictionary<string, FileEntry> rightEntries,
        IReadOnlyDictionary<string, SyncHistoryEntry> historyByPath,
        CompareMethod method)
    {
        var allPaths = leftEntries.Keys
            .Union(rightEntries.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        var diffs = new List<DiffResult>();

        foreach (var path in allPaths)
        {
            leftEntries.TryGetValue(path, out var left);
            rightEntries.TryGetValue(path, out var right);
            historyByPath.TryGetValue(path, out var history);

            var diff = new DiffResult
            {
                RelativePath = path,
                LeftEntry = left,
                RightEntry = right,
                State = ResolveState(left, right, history, method)
            };

            diffs.Add(diff);
        }

        return diffs;
    }

    private static DiffState ResolveState(
        FileEntry? left,
        FileEntry? right,
        SyncHistoryEntry? history,
        CompareMethod method)
    {
        if (left is not null && right is null)
        {
            return history is not null ? DiffState.ApagadoDireita : DiffState.NovoEsquerda;
        }

        if (left is null && right is not null)
        {
            return history is not null ? DiffState.ApagadoEsquerda : DiffState.NovoDireita;
        }

        if (left is null || right is null)
        {
            return DiffState.Erro;
        }

        if (left.IsDirectory || right.IsDirectory)
        {
            if (left.IsDirectory != right.IsDirectory)
            {
                return DiffState.Erro;
            }

            return DiffState.Igual;
        }

        if (AreEqual(left, right, method))
        {
            return DiffState.Igual;
        }

        if (history is not null)
        {
            var leftChanged = HasChangedLeft(left, history, method);
            var rightChanged = HasChangedRight(right, history, method);

            if (leftChanged && rightChanged)
            {
                if (ContentsAppearSynced(left, right, method))
                {
                    return DiffState.Igual;
                }

                return DiffState.Conflito;
            }

            if (leftChanged)
            {
                return DiffState.AlteradoEsquerda;
            }

            if (rightChanged)
            {
                return DiffState.AlteradoDireita;
            }

            // Histórico recente não substitui diferença real entre esquerda e direita
            // (ex.: sincronização parcial — só alguns ficheiros foram copiados).
            if (!AreEqual(left, right, method))
            {
                return left.LastModifiedUtc > right.LastModifiedUtc
                    ? DiffState.AlteradoEsquerda
                    : right.LastModifiedUtc > left.LastModifiedUtc
                        ? DiffState.AlteradoDireita
                        : DiffState.AlteradoAmbos;
            }

            return DiffState.Igual;
        }

        if (left.LastModifiedUtc > right.LastModifiedUtc)
        {
            return DiffState.AlteradoEsquerda;
        }

        if (right.LastModifiedUtc > left.LastModifiedUtc)
        {
            return DiffState.AlteradoDireita;
        }

        return DiffState.AlteradoAmbos;
    }

    private static bool AreEqual(FileEntry left, FileEntry right, CompareMethod method)
    {
        if (left.SizeBytes != right.SizeBytes)
        {
            return false;
        }

        return method == CompareMethod.Hash
            ? string.Equals(left.Hash, right.Hash, StringComparison.OrdinalIgnoreCase)
            : TimesEqual(left.LastModifiedUtc, right.LastModifiedUtc);
    }

    private static bool TimesEqual(DateTimeOffset left, DateTimeOffset right) =>
        Math.Abs((left - right).TotalSeconds) <= TimeComparisonTolerance.TotalSeconds;

    /// <summary>
    /// Esquerda e direita parecem já sincronizadas (histórico desactualizado após sync parcial).
    /// </summary>
    private static bool ContentsAppearSynced(FileEntry left, FileEntry right, CompareMethod method)
    {
        if (left.SizeBytes != right.SizeBytes)
        {
            return false;
        }

        if (method == CompareMethod.Hash)
        {
            if (!string.IsNullOrWhiteSpace(left.Hash) && !string.IsNullOrWhiteSpace(right.Hash))
            {
                return string.Equals(left.Hash, right.Hash, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!string.IsNullOrWhiteSpace(left.Hash) && !string.IsNullOrWhiteSpace(right.Hash))
        {
            return string.Equals(left.Hash, right.Hash, StringComparison.OrdinalIgnoreCase);
        }

        return TimesEqual(left.LastModifiedUtc, right.LastModifiedUtc);
    }

    private static bool HasChangedLeft(FileEntry left, SyncHistoryEntry history, CompareMethod method)
    {
        if (method == CompareMethod.Hash && !string.IsNullOrWhiteSpace(history.LastHash))
        {
            return !string.Equals(left.Hash, history.LastHash, StringComparison.OrdinalIgnoreCase);
        }

        return history.LastLeftModifiedUtc is null
               || !TimesEqual(left.LastModifiedUtc, history.LastLeftModifiedUtc.Value);
    }

    private static bool HasChangedRight(FileEntry right, SyncHistoryEntry history, CompareMethod method)
    {
        if (method == CompareMethod.Hash && !string.IsNullOrWhiteSpace(history.LastHash))
        {
            return !string.Equals(right.Hash, history.LastHash, StringComparison.OrdinalIgnoreCase);
        }

        return history.LastRightModifiedUtc is null
               || !TimesEqual(right.LastModifiedUtc, history.LastRightModifiedUtc.Value);
    }
}
