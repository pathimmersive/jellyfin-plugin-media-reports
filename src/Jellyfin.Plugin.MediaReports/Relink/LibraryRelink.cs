using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Relink;

/// <summary>
/// Keeps issues with their title when its file is replaced: moves them to the one title that
/// replaced a removed one, and marks open issues to check when a file changes at the same path.
/// Library events start the work; <see cref="Reconcile"/> catches what they miss.
/// </summary>
public sealed class LibraryRelink : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IRelinkLibrary _library;
    private readonly ReportStore _store;
    private readonly ILogger<LibraryRelink> _logger;
    private readonly object _lock = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRelink"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager, for its events.</param>
    /// <param name="library">The library, as matching needs it.</param>
    /// <param name="store">The report store.</param>
    /// <param name="logger">The logger.</param>
    public LibraryRelink(ILibraryManager libraryManager, IRelinkLibrary library, ReportStore store, ILogger<LibraryRelink> logger)
    {
        _libraryManager = libraryManager;
        _library = library;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Gets a task that completes when the work queued so far has run.
    /// </summary>
    public Task WhenIdle
    {
        get
        {
            lock (_lock)
            {
                return _tail;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves the issues of a title that left the library to the one title that matches it.
    /// </summary>
    /// <param name="itemId">The removed item.</param>
    /// <param name="knownRemoved">True when the library said it was removed, so it is not looked up.</param>
    /// <returns>How many issues moved.</returns>
    public int Removed(Guid itemId, bool knownRemoved = true)
    {
        var title = _store.GetReportedTitle(itemId);
        if (title is null || !RelinkMatcher.HasIds(title) || (!knownRemoved && _library.Contains(itemId)))
        {
            return 0;
        }

        if (RelinkMatcher.Match(title, _library) is not { } match)
        {
            _logger.LogInformation("Media reports on {Item} stay where they are: no single title in the library matches it", itemId);
            return 0;
        }

        var moved = _store.MoveItemIssues(itemId, match);
        _logger.LogInformation("Moved {Count} media report issues from {Old} to {New}, which replaced it", moved, itemId, match);
        return moved;
    }

    /// <summary>
    /// Moves to an added title the issues of any title that left the library and that it matches.
    /// </summary>
    /// <param name="added">The added title.</param>
    /// <returns>How many issues moved.</returns>
    public int Added(RelinkTitle added)
    {
        if (!RelinkMatcher.HasIds(added))
        {
            return 0;
        }

        var moved = 0;
        foreach (var title in _store.GetReportedTitles())
        {
            if (title.Id != added.Id && RelinkMatcher.CouldMatch(title, added) && !_library.Contains(title.Id))
            {
                moved += Removed(title.Id);
            }
        }

        return moved;
    }

    /// <summary>
    /// Compares a movie's or episode's file with what its open issues were last checked against.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>How many issues became Replaced – verify.</returns>
    public int FileSeen(Guid itemId)
    {
        if (!_store.HasOpenIssues(itemId) || _library.FileOf(itemId) is not { } file)
        {
            return 0;
        }

        var replaced = _store.CheckFile(itemId, file.Size, file.Modified);
        if (replaced > 0)
        {
            _logger.LogInformation("The file of {Item} changed; {Count} open media report issues are now Replaced – verify", itemId, replaced);
        }

        return replaced;
    }

    /// <summary>
    /// Looks again at every issue: moves those whose title left the library when one title
    /// matches it, and checks the file of every movie or episode with open issues.
    /// </summary>
    /// <param name="cancellationToken">Stops between titles.</param>
    /// <returns>How many issues moved and how many became Replaced – verify.</returns>
    public (int Moved, int Replaced) Reconcile(CancellationToken cancellationToken = default)
    {
        var moved = 0;
        foreach (var title in _store.GetReportedTitles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_library.Contains(title.Id))
            {
                moved += Removed(title.Id);
            }
        }

        var replaced = 0;
        foreach (var itemId in _store.GetIssueItemIds(openOnly: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            replaced += FileSeen(itemId);
        }

        return (moved, replaced);
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var id = e.Item.Id;
        Queue(() => Removed(id));
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (JellyfinRelinkLibrary.Title(e.Item) is { } title)
        {
            Queue(() => Added(title));
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        // Provider ids often arrive with the metadata refresh after the item was added, so an
        // update can complete a match the addition could not.
        if (JellyfinRelinkLibrary.Title(e.Item) is { } title)
        {
            Queue(() =>
            {
                Added(title);
                if (title.Type is "Movie" or "Episode")
                {
                    FileSeen(title.Id);
                }
            });
        }
    }

    // Runs the work after what is already queued, off the library's thread; a failure is logged
    // and never reaches Jellyfin.
    private void Queue(Action work)
    {
        lock (_lock)
        {
            _tail = _tail.ContinueWith(
                _ =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Media reports could not follow a library change; the daily re-link will look again");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }
}
