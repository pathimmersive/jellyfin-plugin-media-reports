using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// How a request ended.
/// </summary>
public enum Outcome
{
    /// <summary>It worked.</summary>
    Ok,

    /// <summary>The request is invalid.</summary>
    BadRequest,

    /// <summary>Reporting is switched off.</summary>
    Forbidden,

    /// <summary>The item or report doesn't exist for the caller.</summary>
    NotFound,

    /// <summary>The request clashes with what is stored.</summary>
    Conflict,

    /// <summary>The caller sent too many in a short time.</summary>
    TooManyRequests,
}

/// <summary>
/// A result with an error code when it didn't work.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="Outcome">How it ended.</param>
/// <param name="Value">The value, when it worked.</param>
/// <param name="Code">The error code, when it didn't.</param>
/// <param name="Message">A short explanation, when it didn't.</param>
/// <param name="IssueIds">The issues a refusal names, when it names any.</param>
public sealed record ServiceResult<T>(Outcome Outcome, T? Value = default, string? Code = null, string? Message = null, IReadOnlyList<long>? IssueIds = null)
{
    /// <summary>Gets the caller's open report a refusal names, such as the one an already-reported filing duplicates.</summary>
    public long? ReportId { get; init; }

    /// <summary>Gets how many seconds a refused caller waits before trying again, or null.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// A successful result.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static ServiceResult<T> Success(T value) => new(Outcome.Ok, value);

    /// <summary>
    /// A refusal.
    /// </summary>
    /// <param name="outcome">Why.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A short explanation.</param>
    /// <returns>The result.</returns>
    public static ServiceResult<T> Refuse(Outcome outcome, string code, string message) => new(outcome, default, code, message);

    /// <summary>
    /// A refusal that names issues.
    /// </summary>
    /// <param name="outcome">Why.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A short explanation.</param>
    /// <param name="issueIds">The issues it names.</param>
    /// <returns>The result.</returns>
    public static ServiceResult<T> Refuse(Outcome outcome, string code, string message, IReadOnlyList<long> issueIds) => new(outcome, default, code, message, issueIds);
}

/// <summary>
/// A report the signed-in user files.
/// </summary>
/// <param name="ItemId">The movie, episode, season or series.</param>
/// <param name="ReasonCode">The reason's code.</param>
/// <param name="Note">An optional note, up to 1,000 characters.</param>
public sealed record FileReportRequest(Guid ItemId, string ReasonCode, string? Note);

/// <summary>
/// An error answer.
/// </summary>
/// <param name="Code">The error code.</param>
/// <param name="Message">A short explanation.</param>
/// <param name="ReportId">The caller's open report a refusal names; left out of the answer when null.</param>
public sealed record ApiError(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ReportId = null);

/// <summary>
/// What the user is about to report.
/// </summary>
public sealed record ReportContext
{
    /// <summary>Gets the item id.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the title.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the type: Movie, Episode, Season or Series.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the series name, for an episode or season; null otherwise.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season number of an episode, as Jellyfin's ParentIndexNumber; null otherwise.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Gets the episode number of an episode or the season number of a season, as Jellyfin's IndexNumber; null otherwise.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Gets the year the title came out, or null.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the longest note a report takes, in characters.</summary>
    public int NoteMaxLength { get; init; }

    /// <summary>Gets the reasons on offer, in order.</summary>
    public IReadOnlyList<ReasonOption> Reasons { get; init; } = [];

    /// <summary>Gets the caller's own open reports on this title.</summary>
    public IReadOnlyList<OpenReport> OpenReports { get; init; } = [];

    /// <summary>Gets the playback that would be attached, or null.</summary>
    public AttachedPlayback? Playback { get; init; }
}

/// <summary>
/// A reason on offer.
/// </summary>
/// <param name="Code">The code.</param>
/// <param name="Label">The label.</param>
public sealed record ReasonOption(string Code, string Label);

/// <summary>
/// One of the caller's open reports.
/// </summary>
/// <param name="Id">The report.</param>
/// <param name="ReasonCode">The reason's code.</param>
/// <param name="ReasonLabel">The reason's current label.</param>
/// <param name="Status">The status code from the reporter set.</param>
public sealed record OpenReport(long Id, string ReasonCode, string ReasonLabel, string Status);

/// <summary>
/// The playback a report would carry.
/// </summary>
/// <param name="DeviceName">The device.</param>
/// <param name="Client">The client app.</param>
/// <param name="State">Playing, Stopped or Ended.</param>
/// <param name="LastSeen">When the server last saw it.</param>
public sealed record AttachedPlayback(string? DeviceName, string? Client, string State, DateTimeOffset LastSeen);

/// <summary>
/// A filed report, as the reporter sees it.
/// </summary>
public sealed record FiledReportView
{
    /// <summary>Gets the report id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the issue it joined.</summary>
    public long IssueId { get; init; }

    /// <summary>Gets the item.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the reason's code.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the status code from the reporter set (new, investigating, fixed, wont-fix, duplicate, cant-reproduce).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets when it was filed.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the playback attached, or null.</summary>
    public AttachedPlayback? Playback { get; init; }
}

/// <summary>
/// The caller's reports and how many have news.
/// </summary>
public sealed record MyReports
{
    /// <summary>Gets the reports, newest first.</summary>
    public IReadOnlyList<MyReportRow> Reports { get; init; } = [];

    /// <summary>Gets how many are unread, across every page.</summary>
    public int Unread { get; init; }

    /// <summary>Gets the page, from 1.</summary>
    public int Page { get; init; }

    /// <summary>Gets the most reports a page holds.</summary>
    public int PageSize { get; init; }

    /// <summary>Gets how many reports the list holds, across every page.</summary>
    public int Total { get; init; }
}

/// <summary>
/// One of the caller's reports in their list.
/// </summary>
public sealed record MyReportRow
{
    /// <summary>Gets the report id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the reported item.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the title as the reporter sees it.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the series name, for an episode or season; null otherwise.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season number of an episode, as Jellyfin's ParentIndexNumber; null otherwise.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Gets the episode number of an episode or the season number of a season, as Jellyfin's IndexNumber; null otherwise.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Gets the year the title came out, or null.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the reason code, stable across renames.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the reason's current label.</summary>
    public string ReasonLabel { get; init; } = string.Empty;

    /// <summary>Gets when it was filed.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the status code from the reporter set (new, investigating, fixed, wont-fix, duplicate, cant-reproduce).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the status as the reporter sees it.</summary>
    public string StatusLabel { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the status changed since the reporter last opened it.</summary>
    public bool Unread { get; init; }

    /// <summary>Gets when a closed report is purged, or null while it is open or purging is off.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Gets how the expiry reads, such as "Expires in 12 months", or null without one.</summary>
    public string? ExpiresLabel { get; init; }

    /// <summary>Gets a value indicating whether the report is closed, so the caller can remove it from their list.</summary>
    public bool CanHide { get; init; }

    /// <summary>Gets a value indicating whether the report is New, so the caller can still withdraw it.</summary>
    public bool CanWithdraw { get; init; }
}

/// <summary>
/// How many reports were removed from the caller's list.
/// </summary>
public sealed record HiddenReports
{
    /// <summary>Gets the count.</summary>
    public int Hidden { get; init; }
}

/// <summary>
/// One of the caller's reports, opened.
/// </summary>
public sealed record MyReportDetail
{
    /// <summary>Gets the report id.</summary>
    public long Id { get; init; }

    /// <summary>Gets the reported item.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the title as the reporter sees it.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the series name, for an episode or season; null otherwise.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season number of an episode, as Jellyfin's ParentIndexNumber; null otherwise.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Gets the episode number of an episode or the season number of a season, as Jellyfin's IndexNumber; null otherwise.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Gets the year the title came out, or null.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the reason code.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the reason's current label.</summary>
    public string ReasonLabel { get; init; } = string.Empty;

    /// <summary>Gets the reporter's note, or null.</summary>
    public string? Note { get; init; }

    /// <summary>Gets the device the playback was on, or null.</summary>
    public string? Device { get; init; }

    /// <summary>Gets when it was filed.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the status code from the reporter set (new, investigating, fixed, wont-fix, duplicate, cant-reproduce).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the status as the reporter sees it.</summary>
    public string StatusLabel { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the reporter can still withdraw it.</summary>
    public bool CanWithdraw { get; init; }

    /// <summary>Gets the filing and every status change since, oldest first.</summary>
    public IReadOnlyList<MyReportStep> History { get; init; } = [];
}

/// <summary>
/// A step in a report's history as the reporter sees it.
/// </summary>
/// <param name="At">When.</param>
/// <param name="Label">"Reported", or the status it moved to.</param>
/// <param name="Status">The code of the status it moved to, from the reporter set; null for the filing.</param>
public sealed record MyReportStep(DateTimeOffset At, string Label, string? Status);
