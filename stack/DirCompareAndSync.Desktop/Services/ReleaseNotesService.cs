using System.Text;
using System.Text.Json;
using DirCompareAndSync.Desktop.Deploy;

namespace DirCompareAndSync.Desktop.Services;

public sealed class ReleaseNotesEntry
{
    public string Version { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Highlights { get; set; } = [];
}

internal sealed class ReleaseNotesCatalog
{
    public List<ReleaseNotesEntry> Versions { get; set; } = [];
}

public static class ReleaseNotesService
{
    private static readonly Lazy<IReadOnlyList<ReleaseNotesEntry>> Catalog = new(LoadCatalog);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DirCompareAndSync/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public static ReleaseNotesEntry? GetForCurrentVersion() =>
        TryGetForVersion(AppInfo.VersionDisplay) ?? TryGetForVersion(AppInfo.AssemblyVersion);

    /// <summary>
    /// Notas de todas as versões posteriores a <paramref name="fromVersionExclusive"/>
    /// até <paramref name="toVersionInclusive"/> (inclusive), ordenadas da mais antiga para a mais recente.
    /// </summary>
    public static IReadOnlyList<ReleaseNotesEntry> GetNotesSinceVersion(
        string? fromVersionExclusive,
        string? toVersionInclusive,
        IEnumerable<ReleaseNotesEntry>? catalog = null)
    {
        var fromKey = ResolveTechnicalVersion(fromVersionExclusive);
        var toKey = ResolveTechnicalVersion(toVersionInclusive);
        if (!System.Version.TryParse(fromKey, out var fromV) || !System.Version.TryParse(toKey, out var toV))
        {
            return [];
        }

        return (catalog ?? Catalog.Value)
            .Select(CloneEntry)
            .Where(e =>
            {
                EnsureHighlights(e);
                if (e.Highlights.Count == 0)
                {
                    return false;
                }

                var entryV = ParseSortableVersion(e.Version);
                return entryV > fromV && entryV <= toV;
            })
            .OrderBy(e => ParseSortableVersion(e.Version))
            .ToList();
    }

    public static async Task<IReadOnlyList<ReleaseNotesEntry>> TryResolveRangeForUpdateAsync(
        string? targetVersion,
        string? installedVersion,
        CancellationToken cancellationToken = default)
    {
        var remoteCatalog = await TryFetchFullCatalogAsync(targetVersion, cancellationToken);
        var catalog = MergeCatalogs(remoteCatalog, Catalog.Value);
        var range = GetNotesSinceVersion(installedVersion, targetVersion, catalog);
        if (range.Count > 0)
        {
            return range;
        }

        var single = await TryResolveForUpdateAsync(targetVersion, cancellationToken);
        return single is not null ? [single] : [];
    }

    public static string FormatCumulativeHighlights(IReadOnlyList<ReleaseNotesEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            EnsureHighlights(entry);
            var versionLabel = FormatTechnicalVersion(entry.Version);
            if (string.IsNullOrWhiteSpace(versionLabel))
            {
                versionLabel = entry.Display;
            }

            builder.AppendLine($"Versão {versionLabel}");
            builder.AppendLine(FormatHighlights(entry.Highlights));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string BuildCumulativeNotesTitle(
        string? fromVersionExclusive,
        string? toVersionInclusive,
        int entryCount)
    {
        var toLabel = FormatTechnicalVersion(toVersionInclusive);
        if (string.IsNullOrWhiteSpace(toLabel))
        {
            toLabel = "nova versão";
        }

        if (entryCount <= 1)
        {
            return $"Novidades da versão {toLabel}";
        }

        var fromLabel = FormatTechnicalVersion(fromVersionExclusive);
        if (string.IsNullOrWhiteSpace(fromLabel))
        {
            return $"Novidades até à versão {toLabel}";
        }

        return $"Novidades desde a versão {fromLabel} até à {toLabel}";
    }

    public static async Task<ReleaseNotesEntry?> TryResolveForUpdateAsync(
        string? targetVersion,
        CancellationToken cancellationToken = default)
    {
        var local = TryGetForVersion(targetVersion);

        var jsonTask = TryFetchRemoteAsync(targetVersion, cancellationToken);
        var apiTask = TryFetchFromGitHubReleaseApiAsync(targetVersion, cancellationToken);
        await Task.WhenAll(jsonTask, apiTask);

        return PickBestNotes(local, await jsonTask, await apiTask);
    }

    private static ReleaseNotesEntry? PickBestNotes(params ReleaseNotesEntry?[] candidates)
    {
        var withHighlights = candidates
            .Where(c => c?.Highlights is { Count: > 0 })
            .OrderByDescending(c => c!.Highlights.Count)
            .FirstOrDefault();
        if (withHighlights is not null)
        {
            return withHighlights;
        }

        return candidates.FirstOrDefault(c => c is not null);
    }

    private static async Task<IReadOnlyList<ReleaseNotesEntry>> TryFetchFullCatalogAsync(
        string? version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return [];
        }

        var tag = FormatReleaseTag(version);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return [];
        }

        var url = $"{AppDeployInfo.GitHubReleasesRepoUrl}/releases/download/{tag}/release-notes.json";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ParseFullCatalogAsync(stream, cancellationToken);
        }
        catch
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<ReleaseNotesEntry>> ParseFullCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var catalog = await JsonSerializer.DeserializeAsync<ReleaseNotesCatalog>(stream, JsonOptions, cancellationToken);
        if (catalog?.Versions is null || catalog.Versions.Count == 0)
        {
            return [];
        }

        foreach (var entry in catalog.Versions)
        {
            EnsureHighlights(entry);
        }

        return catalog.Versions
            .Select(CloneEntry)
            .OrderBy(e => ParseSortableVersion(e.Version))
            .ToList();
    }

    private static IReadOnlyList<ReleaseNotesEntry> MergeCatalogs(
        IReadOnlyList<ReleaseNotesEntry> primary,
        IReadOnlyList<ReleaseNotesEntry> fallback)
    {
        var merged = new Dictionary<string, ReleaseNotesEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in fallback.Concat(primary))
        {
            var key = ResolveTechnicalVersion(entry.Version);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            merged[key] = CloneEntry(entry);
        }

        return merged.Values
            .OrderBy(e => ParseSortableVersion(e.Version))
            .ToList();
    }

    private static ReleaseNotesEntry CloneEntry(ReleaseNotesEntry entry) =>
        new()
        {
            Version = entry.Version,
            Display = entry.Display,
            Title = entry.Title,
            Highlights = entry.Highlights?.ToList() ?? []
        };

    /// <summary>
    /// Converte versão de display (ex.: 2.20) para forma técnica comparável (2.0.20).
    /// </summary>
    public static string ResolveTechnicalVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0";
        }

        var normalized = NormalizeVersionKey(version);
        if (!System.Version.TryParse(normalized, out var parsed))
        {
            return normalized;
        }

        if (parsed.Build >= 0)
        {
            return normalized;
        }

        if (parsed.Minor >= 10)
        {
            return $"{parsed.Major}.0.{parsed.Minor}";
        }

        return normalized;
    }

    private static System.Version ParseSortableVersion(string? version) =>
        System.Version.TryParse(ResolveTechnicalVersion(version), out var parsed)
            ? parsed
            : new System.Version(0, 0);

    public static string BuildCumulativeDisplayMessage(string intro, IReadOnlyList<ReleaseNotesEntry> entries)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(intro))
        {
            builder.AppendLine(intro.Trim());
            builder.AppendLine();
        }

        var highlights = FormatCumulativeHighlights(entries);
        if (!string.IsNullOrWhiteSpace(highlights))
        {
            builder.AppendLine(highlights);
        }

        var url = entries.Count > 0 ? GetReleasesPageUrl(entries[^1]) : null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            builder.AppendLine();
            builder.Append(url);
        }

        return builder.ToString().TrimEnd();
    }

    private static async Task<ReleaseNotesEntry?> TryFetchRemoteAsync(
        string? version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var tag = FormatReleaseTag(version);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var url = $"{AppDeployInfo.GitHubReleasesRepoUrl}/releases/download/{tag}/release-notes.json";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ParseReleaseNotesCatalogAsync(stream, version, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ReleaseNotesEntry?> ParseReleaseNotesCatalogAsync(
        Stream stream,
        string version,
        CancellationToken cancellationToken)
    {
        var catalog = await JsonSerializer.DeserializeAsync<ReleaseNotesCatalog>(stream, JsonOptions, cancellationToken);
        if (catalog?.Versions is null || catalog.Versions.Count == 0)
        {
            return null;
        }

        foreach (var entry in catalog.Versions)
        {
            EnsureHighlights(entry);
            if (EntryMatchesVersion(entry, version))
            {
                return entry;
            }
        }

        // Sem correspondência exacta: NÃO devolver outra versão (evita mostrar,
        // p.ex., notas da 2.18 quando a nova versão é a 2.19).
        return null;
    }

    private static async Task<ReleaseNotesEntry?> TryFetchFromGitHubReleaseApiAsync(
        string? version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var tag = FormatReleaseTag(version);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var slug = AppDeployInfo.GitHubReleasesRepoUrl
            .Replace("https://github.com/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var url = $"https://api.github.com/repos/{slug}/releases/tags/{tag}";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
            var highlights = ParseHighlightsFromReleaseBody(body);
            if (highlights.Count > 0)
            {
                var display = FormatDisplayVersion(version);
                return new ReleaseNotesEntry
                {
                    Version = NormalizeVersionKey(version),
                    Display = display,
                    Title = $"Novidades da versão {display}",
                    Highlights = highlights
                };
            }

            if (root.TryGetProperty("assets", out var assetsProp))
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (!string.Equals(name, "release-notes.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp)
                        ? urlProp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        continue;
                    }

                    using var notesResponse = await HttpClient.GetAsync(downloadUrl, cancellationToken);
                    if (!notesResponse.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    await using var notesStream = await notesResponse.Content.ReadAsStreamAsync(cancellationToken);
                    var fromAsset = await ParseReleaseNotesCatalogAsync(notesStream, version, cancellationToken);
                    if (fromAsset is not null)
                    {
                        return fromAsset;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ParseHighlightsFromReleaseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var highlights = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("• ", StringComparison.Ordinal))
            {
                highlights.Add(trimmed[2..].Trim());
            }
        }

        return highlights;
    }

    public static string FormatDisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var normalized = NormalizeVersionKey(version);
        if (System.Version.TryParse(normalized, out var parsed))
        {
            return $"{parsed.Major}.{parsed.Minor:D2}";
        }

        return version.Trim().TrimStart('v', 'V');
    }

    // Versão técnica completa, ex.: "2.0.19" (sem 'v', sem .0 de revisão à direita).
    public static string FormatTechnicalVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? string.Empty : NormalizeVersionKey(version);

    public static ReleaseNotesEntry? TryGetForVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var normalized = NormalizeVersionKey(version);
        foreach (var entry in Catalog.Value)
        {
            if (EntryMatchesVersion(entry, version)
                || NormalizeVersionKey(entry.Version) == normalized
                || NormalizeVersionKey(entry.Display) == normalized)
            {
                return entry;
            }
        }

        return null;
    }

    private static bool EntryMatchesVersion(ReleaseNotesEntry entry, string version) =>
        string.Equals(entry.Display, version, StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.Version, version, StringComparison.OrdinalIgnoreCase)
        || VersionsMatch(entry.Version, version)
        || VersionsMatch(entry.Display, version)
        || string.Equals(FormatDisplayVersion(entry.Version), FormatDisplayVersion(version), StringComparison.OrdinalIgnoreCase)
        || string.Equals(FormatDisplayVersion(entry.Display), FormatDisplayVersion(version), StringComparison.OrdinalIgnoreCase);

    private static void EnsureHighlights(ReleaseNotesEntry entry) =>
        entry.Highlights ??= [];

    public static string FormatReleaseTag(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        if (System.Version.TryParse(trimmed, out var parsed))
        {
            if (parsed.Revision > 0)
            {
                return $"v{parsed.Major}.{parsed.Minor}.{parsed.Build}.{parsed.Revision}";
            }

            if (parsed.Build >= 0)
            {
                return $"v{parsed.Major}.{parsed.Minor}.{parsed.Build}";
            }

            return $"v{parsed.Major}.{parsed.Minor}";
        }

        return $"v{trimmed}";
    }

    public static string FormatHighlights(IReadOnlyList<string>? highlights) =>
        highlights is null || highlights.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                highlights.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => $"• {h.Trim()}"));

    public static string BuildDisplayMessage(string intro, ReleaseNotesEntry notes)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(intro))
        {
            builder.AppendLine(intro.Trim());
            builder.AppendLine();
        }

        var highlights = FormatHighlights(notes.Highlights);
        if (!string.IsNullOrWhiteSpace(highlights))
        {
            builder.AppendLine(highlights);
        }

        var url = GetReleasesPageUrl(notes);
        if (!string.IsNullOrWhiteSpace(url))
        {
            builder.AppendLine();
            builder.Append(url);
        }

        return builder.ToString().TrimEnd();
    }

    public static string? GetReleasesPageUrl(ReleaseNotesEntry? entry)
    {
        var version = entry?.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = AppInfo.AssemblyVersion;
        }

        var tag = FormatReleaseTag(version);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return $"{AppDeployInfo.GitHubReleasesRepoUrl}/releases/latest";
        }

        return $"{AppDeployInfo.GitHubReleasesRepoUrl}/releases/tag/{tag}";
    }

    private static IReadOnlyList<ReleaseNotesEntry> LoadCatalog()
    {
        try
        {
            using var stream = OpenReleaseNotesStream();
            if (stream is null)
            {
                return [];
            }

            var catalog = JsonSerializer.Deserialize<ReleaseNotesCatalog>(
                stream,
                JsonOptions);

            return catalog?.Versions ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Stream? OpenReleaseNotesStream()
    {
        var assembly = typeof(ReleaseNotesService).Assembly;
        return assembly.GetManifestResourceStream("DirCompareAndSync.Desktop.Assets.release-notes.json");
    }

    private static bool VersionsMatch(string left, string right) =>
        string.Equals(NormalizeVersionKey(left), NormalizeVersionKey(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersionKey(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..].TrimStart();
        }
        if (System.Version.TryParse(trimmed, out var parsed))
        {
            var parts = new List<string>
            {
                parsed.Major.ToString(),
                parsed.Minor.ToString()
            };

            if (parsed.Build >= 0)
            {
                parts.Add(parsed.Build.ToString());
            }

            if (parsed.Revision > 0)
            {
                parts.Add(parsed.Revision.ToString());
            }

            return string.Join('.', parts);
        }

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.Length == 3 && char.IsDigit(trimmed[0]) && char.IsDigit(trimmed[1]) && char.IsDigit(trimmed[2]))
        {
            return $"{trimmed[0]}.{trimmed[1]}.{trimmed[2]}";
        }

        return trimmed;
    }
}
