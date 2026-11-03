using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The rules for filing a report: what may be reported, which playback is attached,
/// and when a report is refused. Callers only ever act as themselves.
/// </summary>
public sealed class ReportService
{
    /// <summary>
    /// The longest note a reporter can send.
    /// </summary>
    public const int MaxNoteLength = 1000;

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly PlaybackCapture _capture;
    private readonly ReportStore _store;
    private readonly Func<PluginConfiguration> _config;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportService"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="capture">The recent playbacks.</param>
    /// <param name="store">The report store.</param>
    /// <param name="config">Returns the current configuration; read on every call.</param>
    /// <param name="time">The clock.</param>
    public ReportService(ILibraryManager library, IUserManager users, PlaybackCapture capture, ReportStore store, Func<PluginConfiguration> config, TimeProvider time)
    {
        _library = library;
        _users = users;
        _capture = capture;
        _store = store;
        _config = config;
        _time = time;
    }

    /// <summary>
    /// Describes what the caller is about to report.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="deviceId">The caller's device, preferred when several playbacks are live.</param>
    /// <param name="itemId">The title.</param>
    /// <returns>The context, or a refusal.</returns>
    public ServiceResult<ReportContext> GetContext(Guid userId, string? deviceId, Guid itemId)
    {
        var config = _config();
        if (!config.ReportingEnabled)
        {
            return ReportingOff<ReportContext>();
        }

        var item = FindItem(userId, itemId);
        if (item is null)
        {
            return NotFound<ReportContext>();
        }

        var reasons = config.GetReasons();
        var playback = PickPlayback(userId, deviceId, item);
        return ServiceResult<ReportContext>.Success(new ReportContext
        {
            ItemId = item.Id,
            Name = item.Name,
            Type = TypeName(item),
            Reasons = reasons.Where(r => !r.Retired).Select(r => new ReasonOption(r.Code, r.Label)).ToList(),
            OpenReports = _store.GetOpenReports(userId, item.Id)
                .Select(r => new OpenReport(r.ReportId, r.ReasonCode, Label(reasons, r.ReasonCode), r.Status.ToString()))
                .ToList(),
            Playback = Describe(playback),
        });
    }

    /// <summary>
    /// Files a report, attaching the playback the server picks.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="deviceId">The caller's device.</param>
    /// <param name="request">The report.</param>
    /// <returns>The filed report, or a refusal.</returns>
    public ServiceResult<FiledReportView> File(Guid userId, string? deviceId, FileReportRequest request)
    {
        var config = _config();
        if (!config.ReportingEnabled)
        {
            return ReportingOff<FiledReportView>();
        }

        var item = FindItem(userId, request.ItemId);
        if (item is null)
        {
            return NotFound<FiledReportView>();
        }

        if (!config.GetReasons().Any(r => !r.Retired && string.Equals(r.Code, request.ReasonCode, StringComparison.Ordinal)))
        {
            return ServiceResult<FiledReportView>.Refuse(Outcome.BadRequest, "reason-not-allowed", "That reason isn't on offer.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            return ServiceResult<FiledReportView>.Refuse(Outcome.BadRequest, "note-too-long", $"The note is longer than {MaxNoteLength} characters.");
        }

        if (_store.GetOpenReports(userId, item.Id).Any(r => string.Equals(r.ReasonCode, request.ReasonCode, StringComparison.Ordinal)))
        {
            return ServiceResult<FiledReportView>.Refuse(Outcome.Conflict, "already-reported", "You already have an open report on this title for that reason.");
        }

        var playback = PickPlayback(userId, deviceId, item);
        var series = item switch
        {
            Episode e => _library.GetItemById(e.SeriesId),
            Season s => _library.GetItemById(s.SeriesId),
            _ => null,
        };
        var file = item is Movie or Episode && !string.IsNullOrEmpty(item.Path) && System.IO.File.Exists(item.Path)
            ? new FileInfo(item.Path)
            : null;

        var filed = _store.AddReport(new NewReport
        {
            ReporterUserId = userId,
            ReporterName = _users.GetUserById(userId)?.Username,
            ItemId = item.Id,
            ItemType = TypeName(item),
            ItemName = item.Name,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds ?? []),
            SeriesId = series?.Id,
            SeriesName = series?.Name ?? (item as IHasSeries)?.SeriesName,
            SeriesProviderIds = new Dictionary<string, string>(series?.ProviderIds ?? []),
            SeasonNumber = item switch
            {
                Episode e => e.ParentIndexNumber,
                Season s => s.IndexNumber,
                _ => null,
            },
            EpisodeNumber = (item as Episode)?.IndexNumber,
            FilePath = item.Path,
            FileSize = file?.Length,
            FileModified = file is null ? null : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            ReasonCode = request.ReasonCode,
            Note = note,
            Snapshot = playback is null ? null : ReportSnapshot.FromPlayback(playback),
        });

        var issue = _store.GetIssue(filed.IssueId)!;
        return ServiceResult<FiledReportView>.Success(new FiledReportView
        {
            Id = filed.ReportId,
            IssueId = filed.IssueId,
            ItemId = item.Id,
            ReasonCode = request.ReasonCode,
            Status = issue.Status.ToString(),
            CreatedAt = _time.GetUtcNow(),
            Playback = Describe(playback),
        });
    }

    /// <summary>
    /// Lists the caller's reports, newest first, with the unread count, leaving out those they
    /// removed from their list. A closed report says when the purge will delete it. Works while
    /// reporting is off.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <returns>The reports.</returns>
    public ServiceResult<MyReports> Mine(Guid userId)
    {
        var config = _config();
        var reasons = config.GetReasons();
        var now = _time.GetUtcNow();
        var rows = _store.GetReporterReports(userId).Where(r => !r.Hidden).Select(r =>
        {
            DateTimeOffset? expires = r.ClosedAt is { } closed && config.ReportRetentionDays > 0 ? closed.AddDays(config.ReportRetentionDays) : null;
            return new MyReportRow
            {
                Id = r.Report.Id,
                ItemId = r.Report.ItemId,
                Title = ReporterText.Title(r.Report),
                ReasonLabel = Label(reasons, r.Report.ReasonCode),
                CreatedAt = r.Report.CreatedAt,
                Status = r.Status.ToString(),
                StatusLabel = ReporterText.StatusLabel(r.Status),
                Unread = r.Unread,
                ExpiresAt = expires,
                ExpiresLabel = expires is { } at ? ReporterText.ExpiryLabel(at, now) : null,
                CanHide = r.ClosedAt is not null,
                CanWithdraw = r.Status == IssueStatus.New,
            };
        }).ToList();
        return ServiceResult<MyReports>.Success(new MyReports { Reports = rows, Unread = rows.Count(r => r.Unread) });
    }

    /// <summary>
    /// Removes one of the caller's closed reports from their list. Works while reporting is off.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="reportId">The report.</param>
    /// <returns>Success, or a refusal.</returns>
    public ServiceResult<bool> Hide(Guid userId, long reportId) => _store.HideFromReporter(reportId, userId) switch
    {
        Hiding.Hidden => ServiceResult<bool>.Success(true),
        Hiding.NotClosed => ServiceResult<bool>.Refuse(Outcome.Conflict, "not-closed", "The report is still open, so it stays in your list."),
        _ => ReportNotFound<bool>(),
    };

    /// <summary>
    /// Removes every closed report from the caller's list. Works while reporting is off.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <returns>How many were removed.</returns>
    public ServiceResult<HiddenReports> HideClosed(Guid userId)
        => ServiceResult<HiddenReports>.Success(new HiddenReports { Hidden = _store.HideClosedFromReporter(userId) });

    /// <summary>
    /// Opens one of the caller's reports and records that they have seen its current status.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="reportId">The report.</param>
    /// <returns>The report, or not found when it isn't the caller's.</returns>
    public ServiceResult<MyReportDetail> MineDetail(Guid userId, long reportId)
    {
        var mine = _store.GetReporterReports(userId).FirstOrDefault(r => r.Report.Id == reportId);
        if (mine is null)
        {
            return ReportNotFound<MyReportDetail>();
        }

        var report = mine.Report;
        var changes = _store.GetIssueEvents(report.IssueId)
            .Where(e => e.Kind == "status changed" && e.At >= report.CreatedAt)
            .Select(e => (e.At, Status: ReporterText.StatusChangedTo(e.Detail)))
            .Where(e => e.Status is not null)
            .Select(e => new MyReportStep(e.At, ReporterText.StatusLabel(e.Status!.Value)));
        _store.MarkReporterSeen(report.Id);
        return ServiceResult<MyReportDetail>.Success(new MyReportDetail
        {
            Id = report.Id,
            ItemId = report.ItemId,
            Title = ReporterText.Title(report),
            ReasonLabel = Label(_config().GetReasons(), report.ReasonCode),
            Note = report.Note,
            Device = report.Device,
            CreatedAt = report.CreatedAt,
            Status = mine.Status.ToString(),
            StatusLabel = ReporterText.StatusLabel(mine.Status),
            CanWithdraw = mine.Status == IssueStatus.New,
            History = [new MyReportStep(report.CreatedAt, "Reported"), .. changes],
        });
    }

    /// <summary>
    /// Withdraws one of the caller's reports while its issue is New.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="reportId">The report.</param>
    /// <returns>Success, or a refusal.</returns>
    public ServiceResult<bool> Withdraw(Guid userId, long reportId)
    {
        if (!_config().ReportingEnabled)
        {
            return ReportingOff<bool>();
        }

        return _store.Withdraw(reportId, userId) switch
        {
            Withdrawal.Withdrawn => ServiceResult<bool>.Success(true),
            Withdrawal.NotNew => ServiceResult<bool>.Refuse(Outcome.Conflict, "not-new", "The report is already being looked at, so it can't be withdrawn."),
            _ => ReportNotFound<bool>(),
        };
    }

    private BaseItem? FindItem(Guid userId, Guid itemId)
    {
        var user = _users.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var item = _library.GetItemById<BaseItem>(itemId, user);
        return item is Movie or Episode or Season or Series ? item : null;
    }

    // A movie or episode carries the caller's live playback of it, preferring the calling
    // device, else their newest buffered one; a season or series carries none.
    private PlaybackEntry? PickPlayback(Guid userId, string? deviceId, BaseItem item)
    {
        if (item is not (Movie or Episode))
        {
            return null;
        }

        var playbacks = _capture.GetPlaybacks(userId).Where(p => p.ItemId == item.Id).ToList();
        return playbacks.FirstOrDefault(p => p.State == PlaybackState.Playing && deviceId is not null && string.Equals(p.DeviceId, deviceId, StringComparison.Ordinal))
            ?? playbacks.FirstOrDefault(p => p.State == PlaybackState.Playing)
            ?? playbacks.FirstOrDefault();
    }

    private static AttachedPlayback? Describe(PlaybackEntry? playback) =>
        playback is null ? null : new AttachedPlayback(playback.DeviceName, playback.Client, playback.State.ToString(), playback.LastSeen);

    private static string Label(IEnumerable<ReasonEntry> reasons, string code) =>
        reasons.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal))?.Label ?? code;

    private static string TypeName(BaseItem item) => item switch
    {
        Movie => "Movie",
        Episode => "Episode",
        Season => "Season",
        Series => "Series",
        _ => item.GetType().Name,
    };

    private static ServiceResult<T> ReportingOff<T>() =>
        ServiceResult<T>.Refuse(Outcome.Forbidden, "reporting-off", "Reporting is switched off on this server.");

    private static ServiceResult<T> NotFound<T>() =>
        ServiceResult<T>.Refuse(Outcome.NotFound, "item-not-found", "That title isn't in your library.");

    private static ServiceResult<T> ReportNotFound<T>() =>
        ServiceResult<T>.Refuse(Outcome.NotFound, "report-not-found", "You have no report with that id.");
}
