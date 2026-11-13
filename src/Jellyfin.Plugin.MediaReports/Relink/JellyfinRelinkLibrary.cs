using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaReports.Relink;

/// <summary>
/// The Jellyfin library, as matching needs it.
/// </summary>
public sealed class JellyfinRelinkLibrary : IRelinkLibrary
{
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinRelinkLibrary"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public JellyfinRelinkLibrary(ILibraryManager library)
    {
        _library = library;
    }

    /// <summary>
    /// Describes a library item for matching.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The title, or null for any other kind of item.</returns>
    public static RelinkTitle? Title(BaseItem item)
    {
        var type = item switch
        {
            Movie => "Movie",
            Episode => "Episode",
            Season => "Season",
            Series => "Series",
            _ => null,
        };
        if (type is null)
        {
            return null;
        }

        var series = item switch
        {
            Episode e => e.Series,
            Season s => s.Series,
            _ => null,
        };
        return new RelinkTitle(
            item.Id,
            type,
            new Dictionary<string, string>(item.ProviderIds ?? []),
            new Dictionary<string, string>(series?.ProviderIds ?? []),
            item switch
            {
                Episode e => e.ParentIndexNumber,
                Season s => s.IndexNumber,
                _ => null,
            },
            (item as Episode)?.IndexNumber);
    }

    /// <inheritdoc />
    public bool Contains(Guid itemId) => _library.GetItemById(itemId) is not null;

    /// <inheritdoc />
    public IReadOnlyList<RelinkTitle> FindByProviderIds(string type, IReadOnlyDictionary<string, string> providerIds) =>
        _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [Kind(type)],
            HasAnyProviderId = new Dictionary<string, string>(providerIds),
            Recursive = true,
            IsVirtualItem = false,
        })
        .Select(Title)
        .OfType<RelinkTitle>()
        .ToList();

    /// <inheritdoc />
    public IReadOnlyList<RelinkTitle> FindInSeries(string type, IReadOnlyDictionary<string, string> seriesProviderIds, int season)
    {
        var titles = new List<RelinkTitle>();
        foreach (var series in FindByProviderIds("Series", seriesProviderIds))
        {
            titles.AddRange(_library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [Kind(type)],
                AncestorIds = [series.Id],
                Recursive = true,
                IsVirtualItem = false,
            })
            .Select(Title)
            .OfType<RelinkTitle>()
            .Where(t => t.Season == season));
        }

        return titles;
    }

    /// <inheritdoc />
    public (long Size, DateTimeOffset Modified)? FileOf(Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is not (Movie or Episode) || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return null;
        }

        var file = new FileInfo(item.Path);
        return (file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static BaseItemKind Kind(string type) => type switch
    {
        "Movie" => BaseItemKind.Movie,
        "Episode" => BaseItemKind.Episode,
        "Season" => BaseItemKind.Season,
        _ => BaseItemKind.Series,
    };
}
