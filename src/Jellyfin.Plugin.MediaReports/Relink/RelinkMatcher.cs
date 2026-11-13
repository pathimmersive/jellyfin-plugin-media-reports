using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaReports.Relink;

/// <summary>
/// A title as matching sees it: its type, provider ids, its series' provider ids and its numbers.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Type">Movie, Episode, Season or Series.</param>
/// <param name="ProviderIds">The title's own provider ids.</param>
/// <param name="SeriesProviderIds">The series' provider ids, for an episode or season.</param>
/// <param name="Season">The season number, for an episode or season.</param>
/// <param name="Episode">The episode number, for an episode.</param>
public sealed record RelinkTitle(
    Guid Id,
    string Type,
    IReadOnlyDictionary<string, string> ProviderIds,
    IReadOnlyDictionary<string, string> SeriesProviderIds,
    int? Season,
    int? Episode);

/// <summary>
/// The library, as matching needs it.
/// </summary>
public interface IRelinkLibrary
{
    /// <summary>
    /// Gets whether an item is still in the library.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True when it is.</returns>
    bool Contains(Guid itemId);

    /// <summary>
    /// Finds titles of a type that have any of these provider ids.
    /// </summary>
    /// <param name="type">Movie, Episode or Series.</param>
    /// <param name="providerIds">The provider ids, keyed by provider.</param>
    /// <returns>Candidate titles; the matcher checks them again.</returns>
    IReadOnlyList<RelinkTitle> FindByProviderIds(string type, IReadOnlyDictionary<string, string> providerIds);

    /// <summary>
    /// Finds episodes or seasons in series that have any of these provider ids.
    /// </summary>
    /// <param name="type">Episode or Season.</param>
    /// <param name="seriesProviderIds">The series' provider ids.</param>
    /// <param name="season">The season number.</param>
    /// <returns>Candidate titles; the matcher checks them again.</returns>
    IReadOnlyList<RelinkTitle> FindInSeries(string type, IReadOnlyDictionary<string, string> seriesProviderIds, int season);

    /// <summary>
    /// Gets the size and modified time of a movie's or episode's file.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The file, or null when the item is not a movie or episode in the library, or its file is missing.</returns>
    (long Size, DateTimeOffset Modified)? FileOf(Guid itemId);
}

/// <summary>
/// Matches a removed title to the one title in the library that replaced it.
/// </summary>
public static class RelinkMatcher
{
    /// <summary>
    /// The providers whose ids identify a title.
    /// </summary>
    public static readonly IReadOnlyList<string> Providers = ["Tmdb", "Tvdb", "Imdb"];

    private static readonly IReadOnlyList<string> EpisodeProviders = ["Tvdb", "Imdb"];

    /// <summary>
    /// Finds the single title in the library that matches a removed one.
    /// </summary>
    /// <param name="removed">The removed title, as its reports recorded it.</param>
    /// <param name="library">The library.</param>
    /// <returns>The matching item, or null when none or more than one matches.</returns>
    public static Guid? Match(RelinkTitle removed, IRelinkLibrary library)
    {
        var matches = removed.Type switch
        {
            "Movie" or "Series" => Own(removed, library, Providers),
            "Episode" => Own(removed, library, EpisodeProviders) is { Count: > 0 } own ? own : BySeries(removed, library),
            "Season" => BySeries(removed, library),
            _ => [],
        };
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Gets whether two titles could be the same one: same type and a shared provider id (or,
    /// for an episode or season, a shared series id and the same numbers).
    /// </summary>
    /// <param name="removed">The removed title.</param>
    /// <param name="added">The added title.</param>
    /// <returns>True when they could match.</returns>
    public static bool CouldMatch(RelinkTitle removed, RelinkTitle added)
    {
        if (!string.Equals(removed.Type, added.Type, StringComparison.Ordinal))
        {
            return false;
        }

        return removed.Type switch
        {
            "Movie" or "Series" => Shares(removed.ProviderIds, added.ProviderIds, Providers),
            "Episode" => Shares(removed.ProviderIds, added.ProviderIds, EpisodeProviders) || SameInSeries(removed, added),
            "Season" => SameInSeries(removed, added),
            _ => false,
        };
    }

    /// <summary>
    /// Gets whether a title has any provider id matching can use.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>True when it has one.</returns>
    public static bool HasIds(RelinkTitle title) =>
        Usable(title.ProviderIds, Providers).Count > 0 || (title.Type is "Episode" or "Season" && Usable(title.SeriesProviderIds, Providers).Count > 0);

    /// <summary>
    /// Gets a title's non-empty ids for the given providers, keys and values lower-cased.
    /// </summary>
    /// <param name="ids">The provider ids.</param>
    /// <param name="providers">The providers to keep.</param>
    /// <returns>The usable ids, keyed by provider.</returns>
    public static IReadOnlyDictionary<string, string> Usable(IReadOnlyDictionary<string, string> ids, IReadOnlyList<string> providers)
    {
        var usable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ids)
        {
            if (!string.IsNullOrWhiteSpace(value) && providers.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                usable[providers.First(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase))] = value.Trim();
            }
        }

        return usable;
    }

    private static List<Guid> Own(RelinkTitle removed, IRelinkLibrary library, IReadOnlyList<string> providers)
    {
        var ids = Usable(removed.ProviderIds, providers);
        if (ids.Count == 0)
        {
            return [];
        }

        return library.FindByProviderIds(removed.Type, ids)
            .Where(t => t.Id != removed.Id && string.Equals(t.Type, removed.Type, StringComparison.Ordinal) && Shares(removed.ProviderIds, t.ProviderIds, providers))
            .Select(t => t.Id)
            .Distinct()
            .ToList();
    }

    private static List<Guid> BySeries(RelinkTitle removed, IRelinkLibrary library)
    {
        var seriesIds = Usable(removed.SeriesProviderIds, Providers);
        if (seriesIds.Count == 0 || removed.Season is not { } season || (removed.Type == "Episode" && removed.Episode is null))
        {
            return [];
        }

        return library.FindInSeries(removed.Type, seriesIds, season)
            .Where(t => t.Id != removed.Id && SameInSeries(removed, t))
            .Select(t => t.Id)
            .Distinct()
            .ToList();
    }

    private static bool SameInSeries(RelinkTitle removed, RelinkTitle other) =>
        string.Equals(removed.Type, other.Type, StringComparison.Ordinal)
        && removed.Season is not null && removed.Season == other.Season
        && (removed.Type != "Episode" || (removed.Episode is not null && removed.Episode == other.Episode))
        && Shares(removed.SeriesProviderIds, other.SeriesProviderIds, Providers);

    private static bool Shares(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b, IReadOnlyList<string> providers)
    {
        var left = Usable(a, providers);
        var right = Usable(b, providers);
        return left.Any(p => right.TryGetValue(p.Key, out var value) && string.Equals(value, p.Value, StringComparison.OrdinalIgnoreCase));
    }
}
