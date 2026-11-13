using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Notifications;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The admin's work on issues: the list and its summary, one issue with the file as
/// Jellyfin has it now, status changes and notes, and the quick actions.
/// </summary>
public sealed class AdminService
{
    /// <summary>
    /// The rows on a page of the issue list.
    /// </summary>
    public const int PageSize = 50;

    /// <summary>
    /// The longest notes an admin can save.
    /// </summary>
    public const int MaxNotesLength = 10000;

    // Three or more episode issues of one season with one reason are shown as a season row.
    private const int RollupThreshold = 3;

    private readonly ReportStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<string> _serverId;
    private readonly ReporterNotifier _notifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminService"/> class.
    /// </summary>
    /// <param name="store">The report store.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="mediaSources">The media source manager, for a file's streams.</param>
    /// <param name="providers">The provider manager, for refreshes and scans.</param>
    /// <param name="fileSystem">The file system, for refresh options.</param>
    /// <param name="config">Returns the current configuration; read on every call.</param>
    /// <param name="serverId">Returns the server id.</param>
    /// <param name="notifier">Tells reporters when their issue closes.</param>
    public AdminService(ReportStore store, ILibraryManager library, IUserManager users, IMediaSourceManager mediaSources, IProviderManager providers, IFileSystem fileSystem, Func<PluginConfiguration> config, Func<string> serverId, ReporterNotifier notifier)
    {
        _store = store;
        _library = library;
        _users = users;
        _mediaSources = mediaSources;
        _providers = providers;
        _fileSystem = fileSystem;
        _config = config;
        _serverId = serverId;
        _notifier = notifier;
    }

    /// <summary>
    /// Counts open issues, their reports, and those new since the admin last looked.
    /// </summary>
    /// <param name="adminId">The admin.</param>
    /// <param name="markSeen">True to record now as the admin's last look, after counting.</param>
    /// <returns>The summary.</returns>
    public AdminSummary GetSummary(Guid adminId, bool markSeen)
    {
        var seen = _store.GetAdminSeen(adminId);
        var open = _store.GetIssuesWithReports(closed: false);
        var summary = new AdminSummary(
            open.Count,
            open.Sum(i => i.Reports.Count),
            open.Count(i => IsNew(i.Reports, seen)),
            Notifications.ButtonWatch.Evaluate(_store, _config(), DateTimeOffset.UtcNow).Stopped);
        if (markSeen)
        {
            _store.MarkAdminSeen(adminId);
        }

        return summary;
    }

    /// <summary>
    /// Lists issues for the Reports tab.
    /// </summary>
    /// <param name="adminId">The admin.</param>
    /// <param name="query">The view, grouping, filters, sort and page.</param>
    /// <returns>One page of rows, or a refusal of an unknown option.</returns>
    public ServiceResult<IssueList> ListIssues(Guid adminId, IssueQuery query)
    {
        if (query.View is not ("open" or "closed" or "all") || query.GroupBy is not ("title" or "titleClient")
            || query.Sort is not ("title" or "reports" or "status" or "last") || query.Page < 1)
        {
            return ServiceResult<IssueList>.Refuse(Outcome.BadRequest, "bad-query", "Unknown view, grouping, sort or page.");
        }

        bool? closed = query.View == "all" ? null : query.View == "closed";
        var seen = _store.GetAdminSeen(adminId);
        var reasons = _config().GetReasons();
        var issues = _store.GetIssuesWithReports(closed);
        var titles = new Dictionary<Guid, (string Title, string? Line)>();
        (string Title, string? Line) TitleOf(StoredReport report) =>
            titles.TryGetValue(report.ItemId, out var t) ? t : titles[report.ItemId] = Title(report);

        var matching = issues
            .Where(i => i.Reports.Count > 0)
            .Where(i => query.Reason is null || string.Equals(i.Issue.ReasonCode, query.Reason, StringComparison.Ordinal))
            .Where(i => query.Client is null || i.Reports.Any(r => string.Equals(r.Client, query.Client, StringComparison.Ordinal)))
            .Where(i => string.IsNullOrWhiteSpace(query.Q) || Matches(i.Reports[0], TitleOf(i.Reports[0]), query.Q))
            .ToList();

        List<IssueRow> rows;
        if (query.GroupBy == "titleClient")
        {
            rows = matching
                .SelectMany(i => i.Reports
                    .GroupBy(r => r.Client)
                    .Where(g => query.Client is null || string.Equals(g.Key, query.Client, StringComparison.Ordinal))
                    .Select(g => Row(i.Issue, g.ToList(), TitleOf(i.Reports[0]), reasons, seen) with { Client = g.Key }))
                .ToList();
        }
        else
        {
            rows = Rollup(matching.Select(i => (i.Issue, i.Reports, Row(i.Issue, i.Reports, TitleOf(i.Reports[0]), reasons, seen))).ToList());
        }

        var sorted = Sort(rows, query.Sort).ToList();
        return ServiceResult<IssueList>.Success(new IssueList
        {
            Rows = sorted.Skip((query.Page - 1) * PageSize).Take(PageSize).ToList(),
            Total = sorted.Count,
            Page = query.Page,
            PageSize = PageSize,
            Reasons = issues.Select(i => i.Issue.ReasonCode).Distinct(StringComparer.Ordinal)
                .Select(code => new ReasonOption(code, Label(reasons, code)))
                .OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Clients = issues.SelectMany(i => i.Reports).Select(r => r.Client).OfType<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase).ToList(),
        });
    }

    /// <summary>
    /// Opens one issue: its reports, history, and the title as Jellyfin has it now.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <returns>The issue, or a refusal.</returns>
    public ServiceResult<IssueDetail> GetIssue(long issueId)
    {
        var issue = _store.GetIssue(issueId);
        if (issue is null)
        {
            return IssueNotFound<IssueDetail>();
        }

        var reports = _store.GetIssueReports(issueId);
        var events = _store.GetIssueEvents(issueId);
        var reasons = _config().GetReasons();
        var newest = reports.FirstOrDefault();
        var (title, line) = newest is null ? (string.Empty, null) : Title(newest);
        var names = new Dictionary<Guid, string?>();
        string? NameOf(Guid? id) => id is not { } user ? null
            : names.TryGetValue(user, out var name) ? name : names[user] = _users.GetUserById(user)?.Username;

        return ServiceResult<IssueDetail>.Success(new IssueDetail
        {
            Id = issue.Id,
            ItemId = issue.ItemId,
            ItemType = newest?.ItemType ?? string.Empty,
            Title = title,
            Line = line,
            ReasonCode = issue.ReasonCode,
            ReasonLabel = Label(reasons, issue.ReasonCode),
            Status = issue.Status.ToString(),
            StatusLabel = issue.Status.DisplayName(),
            Notes = issue.AdminNotes,
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt,
            ClosedAt = issue.ClosedAt,
            People = People(reports),
            Reports = reports.Select(r => new IssueReport
            {
                Id = r.Id,
                CreatedAt = r.CreatedAt,
                ReporterName = r.ReporterName,
                Device = r.Device,
                Client = r.Client,
                PlayMethod = r.PlayMethod,
                TranscodeReasons = string.IsNullOrEmpty(r.TranscodeReasons) ? [] : r.TranscodeReasons.Split(','),
                PositionTicks = Snapshot(r)?.TryGetProperty("positionTicks", out var p) == true && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : null,
                Note = r.Note,
                Snapshot = Snapshot(r),
            }).ToList(),
            History = events.Select(e => new IssueEvent(e.At, e.Kind, e.Detail, NameOf(e.ActorUserId))).ToList(),
            File = File(issue.ItemId, newest),
            ServerId = _serverId(),
        });
    }

    /// <summary>
    /// Changes an issue's status, notes, or both.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <param name="patch">The change.</param>
    /// <param name="adminId">The admin.</param>
    /// <returns>The updated issue, or a refusal; nothing changes when anything is refused.</returns>
    public ServiceResult<IssueDetail> UpdateIssue(long issueId, IssuePatch patch, Guid adminId)
    {
        IssueStatus? status = null;
        if (patch.Status is not null)
        {
            if (ParseStatus(patch.Status) is not { } parsed)
            {
                return StatusNotAllowed<IssueDetail>();
            }

            status = parsed;
        }

        if (patch.Notes is { Length: > MaxNotesLength })
        {
            return ServiceResult<IssueDetail>.Refuse(Outcome.BadRequest, "notes-too-long", $"Notes are longer than {MaxNotesLength:N0} characters.");
        }

        var result = _store.UpdateIssue(issueId, status, patch.Notes, adminId);
        if (result.Closed)
        {
            _ = _notifier.NotifyClosedAsync([issueId]);
        }

        return result.Result switch
        {
            StatusChange.NotFound => IssueNotFound<IssueDetail>(),
            StatusChange.NewerOpenIssue => NewerOpenIssue<IssueDetail>([result.ConflictingIssueId!.Value]),
            _ => GetIssue(issueId),
        };
    }

    /// <summary>
    /// Sets one status on several issues: all of them, or none when any is refused.
    /// </summary>
    /// <param name="request">The issues and the status.</param>
    /// <param name="adminId">The admin.</param>
    /// <returns>The issues set, or a refusal naming the refused issues.</returns>
    public ServiceResult<IReadOnlyList<long>> SetStatuses(IssueStatusRequest request, Guid adminId)
    {
        if (ParseStatus(request.Status) is not { } status)
        {
            return StatusNotAllowed<IReadOnlyList<long>>();
        }

        if (request.IssueIds is not { Count: > 0 })
        {
            return ServiceResult<IReadOnlyList<long>>.Refuse(Outcome.BadRequest, "no-issues", "No issues were given.");
        }

        var closed = new List<long>();
        var refused = _store.SetStatuses(request.IssueIds, status, adminId, closed);
        if (refused.Count == 0)
        {
            _ = _notifier.NotifyClosedAsync(closed);
            return ServiceResult<IReadOnlyList<long>>.Success(request.IssueIds.Distinct().ToList());
        }

        var ids = refused.Select(r => r.IssueId).ToList();
        return refused.Any(r => r.Result.Result == StatusChange.NotFound)
            ? ServiceResult<IReadOnlyList<long>>.Refuse(Outcome.NotFound, "issue-not-found", "Some of those issues don't exist; none was changed.", ids)
            : NewerOpenIssue<IReadOnlyList<long>>(ids, "A newer open issue exists for some of them; none was changed.");
    }

    /// <summary>
    /// Queues Jellyfin's refresh of the title's metadata and images, filling what is missing.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <param name="adminId">The admin.</param>
    /// <returns>Success, or a refusal.</returns>
    public ServiceResult<bool> Refresh(long issueId, Guid adminId) => RunAction(issueId, adminId, item =>
    {
        _providers.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            ForceSave = true,
            IsAutomated = false,
        }, RefreshPriority.High);
        return "Refresh metadata";
    });

    /// <summary>
    /// Queues Jellyfin's scan for new and updated files of the folder holding the title, never the library.
    /// </summary>
    /// <param name="issueId">The issue.</param>
    /// <param name="adminId">The admin.</param>
    /// <returns>Success, or a refusal.</returns>
    public ServiceResult<bool> Scan(long issueId, Guid adminId) => RunAction(issueId, adminId, item =>
    {
        var target = ScanTarget(item);
        _providers.QueueRefresh(target.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.Default,
            IsAutomated = false,
        }, RefreshPriority.High);
        return "Rescan: " + target.Name;
    });

    /// <summary>
    /// Gets what Scan library scans for a title: an episode's season folder or else its series,
    /// a season's own folder or else its series, a series itself, and for a movie the movie
    /// itself, which has no folder item of its own (its parent is the library).
    /// </summary>
    /// <param name="item">The title.</param>
    /// <returns>The item to refresh.</returns>
    public BaseItem ScanTarget(BaseItem item)
    {
        BaseItem? SeriesOf(Guid id) => id.Equals(Guid.Empty) ? null : _library.GetItemById(id);
        return item switch
        {
            Episode e when !e.SeasonId.Equals(Guid.Empty) && _library.GetItemById(e.SeasonId) is Season { Path.Length: > 0 } season => season,
            Episode e => SeriesOf(e.SeriesId) ?? item,
            Season { Path.Length: > 0 } => item,
            Season s => SeriesOf(s.SeriesId) ?? item,
            _ => item,
        };
    }

    private ServiceResult<bool> RunAction(long issueId, Guid adminId, Func<BaseItem, string> run)
    {
        var issue = _store.GetIssue(issueId);
        if (issue is null)
        {
            return IssueNotFound<bool>();
        }

        var item = _library.GetItemById(issue.ItemId);
        if (item is not (Movie or Episode or Season or Series))
        {
            return ServiceResult<bool>.Refuse(Outcome.NotFound, "not-in-library", "This title is no longer in the library.");
        }

        var detail = run(item);
        _store.AddActionEvent(issueId, adminId, detail);
        return ServiceResult<bool>.Success(true);
    }

    private FileDetails File(Guid itemId, StoredReport? newest)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return new FileDetails { InLibrary = false, Path = newest?.FilePath };
        }

        if (item is Season or Series)
        {
            return new FileDetails
            {
                InLibrary = true,
                Path = item.Path,
                DateAdded = item.DateCreated,
                EpisodeCount = _library.GetCount(new InternalItemsQuery
                {
                    AncestorIds = [item.Id],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsVirtualItem = false,
                }),
            };
        }

        return new FileDetails
        {
            InLibrary = true,
            Path = item.Path,
            Size = item.Size,
            DateAdded = item.DateCreated,
            Container = item.Container,
            Bitrate = item.TotalBitrate,
            Streams = _mediaSources.GetMediaStreams(item.Id)
                .Where(s => s.Type is MediaStreamType.Video or MediaStreamType.Audio or MediaStreamType.Subtitle)
                .Select(s => new StreamDetails
                {
                    Type = s.Type.ToString(),
                    Codec = s.Codec,
                    Profile = s.Profile,
                    Width = s.Width,
                    Height = s.Height,
                    VideoRange = s.Type == MediaStreamType.Video ? s.VideoRangeType.ToString() : null,
                    FrameRate = s.RealFrameRate ?? s.AverageFrameRate,
                    Language = s.Language,
                    Title = s.DisplayTitle,
                    IsDefault = s.IsDefault,
                })
                .ToList(),
        };
    }

    private (string Title, string? Line) Title(StoredReport report)
    {
        var season = report.SeasonNumber is { } s ? string.Create(CultureInfo.InvariantCulture, $"S{s:00}") : null;
        return report.ItemType switch
        {
            "Episode" => (report.SeriesName ?? report.ItemName, (season ?? string.Empty) + (report.EpisodeNumber is { } e ? string.Create(CultureInfo.InvariantCulture, $"E{e:00}") : string.Empty) + " · " + report.ItemName),
            "Season" => (report.SeriesName ?? report.ItemName, report.ItemName),
            "Movie" => (report.ItemName, _library.GetItemById(report.ItemId)?.ProductionYear?.ToString(CultureInfo.InvariantCulture)),
            _ => (report.ItemName, null),
        };
    }

    private static bool Matches(StoredReport report, (string Title, string? Line) title, string q) =>
        new[] { report.ItemName, report.SeriesName, title.Title, title.Line }
            .Any(text => text?.Contains(q.Trim(), StringComparison.CurrentCultureIgnoreCase) == true);

    private static IssueRow Row(StoredIssue issue, IReadOnlyList<StoredReport> reports, (string Title, string? Line) title, IReadOnlyList<ReasonEntry> reasons, DateTimeOffset? seen) => new()
    {
        IssueId = issue.Id,
        ItemId = issue.ItemId,
        ItemType = reports[0].ItemType,
        Title = title.Title,
        Line = title.Line,
        ReasonCode = issue.ReasonCode,
        ReasonLabel = Label(reasons, issue.ReasonCode),
        Reports = reports.Count,
        People = People(reports),
        Clients = reports.Select(r => r.Client).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
        PlayMethods = reports.Where(r => r.PlayMethod is not null).GroupBy(r => r.PlayMethod!)
            .Select(g => new PlayMethodCount(g.Key, g.Count())).OrderByDescending(m => m.Count).ToList(),
        Status = issue.Status.ToString(),
        StatusLabel = issue.Status.DisplayName(),
        LastReport = reports.Max(r => r.CreatedAt),
        IsNew = IsNew(reports, seen),
        SeriesId = reports[0].SeriesId,
        SeasonNumber = reports[0].SeasonNumber,
    };

    // Open episode issues of one season and reason, three or more, become one season row.
    private static List<IssueRow> Rollup(List<(StoredIssue Issue, IReadOnlyList<StoredReport> Reports, IssueRow Row)> rows)
    {
        var rolled = rows
            .Where(r => r.Row.ItemType == "Episode" && !r.Issue.Status.IsClosed() && r.Row.SeriesId is not null && r.Row.SeasonNumber is not null)
            .GroupBy(r => (r.Row.SeriesId, r.Row.SeasonNumber, r.Issue.ReasonCode))
            .Where(g => g.Count() >= RollupThreshold)
            .ToList();
        var inSeason = rolled.SelectMany(g => g).Select(r => r.Issue.Id).ToHashSet();
        var result = rows.Where(r => !inSeason.Contains(r.Issue.Id)).Select(r => r.Row).ToList();
        foreach (var group in rolled)
        {
            var children = group.Select(g => g.Row).OrderBy(r => r.Line, StringComparer.Ordinal).ToList();
            var statuses = children.Select(c => c.Status).Distinct(StringComparer.Ordinal).ToList();
            var first = children[0];
            result.Add(new IssueRow
            {
                Kind = "season",
                ItemType = "Season",
                Title = first.Title,
                Line = string.Create(CultureInfo.InvariantCulture, $"Season {first.SeasonNumber}"),
                ReasonCode = first.ReasonCode,
                ReasonLabel = first.ReasonLabel,
                Reports = children.Sum(c => c.Reports),
                People = People(group.SelectMany(g => g.Reports).ToList()),
                Clients = children.SelectMany(c => c.Clients).Distinct(StringComparer.Ordinal).ToList(),
                PlayMethods = children.SelectMany(c => c.PlayMethods).GroupBy(m => m.Method)
                    .Select(g => new PlayMethodCount(g.Key, g.Sum(m => m.Count))).OrderByDescending(m => m.Count).ToList(),
                Status = statuses.Count == 1 ? statuses[0] : "Mixed",
                StatusLabel = statuses.Count == 1 ? first.StatusLabel : "Mixed",
                LastReport = children.Max(c => c.LastReport),
                IsNew = children.Any(c => c.IsNew),
                SeriesId = first.SeriesId,
                SeasonNumber = first.SeasonNumber,
                EpisodeCount = children.Count,
                Children = children,
            });
        }

        return result;
    }

    private static IEnumerable<IssueRow> Sort(List<IssueRow> rows, string sort) => sort switch
    {
        "title" => rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Line, StringComparer.Ordinal).ThenByDescending(r => r.LastReport),
        "reports" => rows.OrderByDescending(r => r.Reports).ThenByDescending(r => r.LastReport),
        "status" => rows.OrderBy(r => StatusOrder(r.Status)).ThenByDescending(r => r.LastReport),
        _ => rows.OrderByDescending(r => r.LastReport).ThenByDescending(r => r.IssueId),
    };

    private static int StatusOrder(string status) => Enum.TryParse<IssueStatus>(status, out var s) ? (int)s : (int)IssueStatus.Investigating;

    private static bool IsNew(IReadOnlyList<StoredReport> reports, DateTimeOffset? seen) =>
        seen is not { } at || reports.Any(r => r.CreatedAt > at);

    // A deleted reporter has no id left, so each of their reports counts as one person.
    private static int People(IReadOnlyList<StoredReport> reports) =>
        reports.Where(r => r.ReporterUserId is not null).Select(r => r.ReporterUserId).Distinct().Count() + reports.Count(r => r.ReporterUserId is null);

    private static JsonElement? Snapshot(StoredReport report) =>
        report.SnapshotJson is null ? null : JsonDocument.Parse(report.SnapshotJson).RootElement.Clone();

    // Only the names the admin may set; Replaced – verify is the plugin's own.
    private static IssueStatus? ParseStatus(string status) =>
        Enum.GetNames<IssueStatus>().Contains(status, StringComparer.Ordinal) && Enum.Parse<IssueStatus>(status) is var parsed && parsed != IssueStatus.ReplacedVerify
            ? parsed
            : null;

    private static string Label(IEnumerable<ReasonEntry> reasons, string code) =>
        reasons.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal))?.Label ?? code;

    private static ServiceResult<T> IssueNotFound<T>() =>
        ServiceResult<T>.Refuse(Outcome.NotFound, "issue-not-found", "There is no such issue.");

    private static ServiceResult<T> StatusNotAllowed<T>() =>
        ServiceResult<T>.Refuse(Outcome.BadRequest, "status-not-allowed", "That status can't be set.");

    private static ServiceResult<T> NewerOpenIssue<T>(IReadOnlyList<long> ids, string message = "A newer open issue exists for this title and reason.") =>
        ServiceResult<T>.Refuse(Outcome.Conflict, "newer-open-issue", message, ids);
}
