using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Notifications;

/// <summary>
/// Tells the admin about new reports: an activity log entry for each, and a message at the
/// Discord and generic webhooks for the reports filed within a minute. Waiting reports live
/// in memory only. Filing a report never waits for an alert or fails because of one.
/// </summary>
public sealed class ReportAlerts
{
    /// <summary>
    /// How long the first report of a group waits for others.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a failed send waits before its one retry.
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a send waits for an answer.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest note an alert carries.
    /// </summary>
    public const int MaxNoteLength = 300;

    /// <summary>
    /// The most reports a Discord message shows; the rest are counted.
    /// </summary>
    public const int MaxEmbeds = 10;

    /// <summary>
    /// The activity log entry type of a filed report.
    /// </summary>
    public const string ActivityType = "MediaReportsReportFiled";

    private const int DiscordAmber = 0xFFB23F;

    // The bodies are JSON for webhooks, not HTML, so names and notes go out as written.
    private static readonly JsonSerializerOptions BodyOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<HttpClient> _http;
    private readonly IActivityManager _activity;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<string> _serverUrl;
    private readonly TimeProvider _time;
    private readonly ILogger<ReportAlerts> _logger;
    private readonly object _lock = new();
    private List<AlertReport> _waiting = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportAlerts"/> class.
    /// </summary>
    /// <param name="http">Gives an HTTP client for a send.</param>
    /// <param name="activity">Jellyfin's activity log.</param>
    /// <param name="config">Returns the current configuration; read when a group is sent.</param>
    /// <param name="serverUrl">Returns the server's address for links, published when one is set.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ReportAlerts(Func<HttpClient> http, IActivityManager activity, Func<PluginConfiguration> config, Func<string> serverUrl, TimeProvider time, ILogger<ReportAlerts> logger)
    {
        _http = http;
        _activity = activity;
        _config = config;
        _serverUrl = serverUrl;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets the send of the group waiting now, for tests to await; null when nothing waits.
    /// </summary>
    internal Task? Sending { get; private set; }

    /// <summary>
    /// Records a stored report in the activity log and adds it to the group waiting to be sent.
    /// Never throws.
    /// </summary>
    /// <param name="report">The report, as stored.</param>
    public void Filed(StoredReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            var alert = Describe(report);
            _ = Record(report, alert);
            lock (_lock)
            {
                _waiting.Add(alert);
                if (_waiting.Count == 1)
                {
                    Sending = SendGroupLater();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't queue the alert for report {Report}", report.Id);
        }
    }

    /// <summary>
    /// Sends one sample report to each address given, at once, and says whether each accepted it.
    /// </summary>
    /// <param name="discordWebhookUrl">The Discord webhook address, or empty.</param>
    /// <param name="webhookUrl">The generic webhook address, or empty.</param>
    /// <returns>One result per address given.</returns>
    public async Task<IReadOnlyList<TestAlertResult>> SendTestAsync(string? discordWebhookUrl, string? webhookUrl)
    {
        var sample = new AlertReport
        {
            Title = "Sample Movie (2026)",
            Reason = "Stutter or jitter",
            ReasonCode = "stutter",
            User = "Media Reports test",
            Device = "Living Room TV",
            Client = "Jellyfin Android TV",
            HowItPlayed = "Transcode (video codec not supported)",
            Note = "This is a test alert from Media Reports. Reports will look like this.",
            FiledAt = _time.GetUtcNow(),
            Link = PageLink(null),
        };
        var results = new List<TestAlertResult>();
        foreach (var (field, address, body) in new[]
        {
            ("discordWebhookUrl", discordWebhookUrl?.Trim(), Discord([sample])),
            ("webhookUrl", webhookUrl?.Trim(), Generic([sample])),
        })
        {
            if (string.IsNullOrEmpty(address))
            {
                continue;
            }

            if (!IsWebAddress(address))
            {
                results.Add(new TestAlertResult(field, false, null, "Enter an http or https address."));
                continue;
            }

            var sent = await Send(address, body).ConfigureAwait(false);
            results.Add(new TestAlertResult(field, sent.Ok, sent.Status, sent.Ok ? "Accepted" : sent.Problem));
        }

        return results;
    }

    /// <summary>
    /// Builds the Discord message for a group of reports: one embed per report, at most ten.
    /// </summary>
    /// <param name="reports">The reports.</param>
    /// <returns>The JSON body.</returns>
    public static string Discord(IReadOnlyList<AlertReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var count = reports.Count == 1 ? "1 new report" : $"{reports.Count} new reports";
        var content = reports.Count > MaxEmbeds ? $"{count}: the first {MaxEmbeds} below, and {reports.Count - MaxEmbeds} more on the Media Reports page." : count;
        var embeds = new JsonArray();
        foreach (var r in reports.Take(MaxEmbeds))
        {
            var fields = new JsonArray();
            foreach (var (name, value) in new[]
            {
                ("Reason", r.Reason),
                ("Reported by", r.User),
                ("Device", string.Join(" · ", new[] { r.Device, r.Client }.Where(v => !string.IsNullOrEmpty(v)))),
                ("How it played", r.HowItPlayed),
            })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    fields.Add(new JsonObject { ["name"] = name, ["value"] = Cut(value, 1024), ["inline"] = true });
                }
            }

            var embed = new JsonObject
            {
                ["title"] = Cut(r.Title, 256),
                ["url"] = r.Link,
                ["color"] = DiscordAmber,
                ["fields"] = fields,
                ["timestamp"] = r.FiledAt.ToString("o", CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrEmpty(r.Note))
            {
                embed["description"] = r.Note;
            }

            embeds.Add(embed);
        }

        return new JsonObject { ["username"] = "Media Reports", ["content"] = content, ["embeds"] = embeds }.ToJsonString(BodyOptions);
    }

    /// <summary>
    /// Builds the generic webhook's JSON body for a group of reports.
    /// </summary>
    /// <param name="reports">The reports.</param>
    /// <param name="sentAt">When it is sent; now when null.</param>
    /// <returns>The JSON body.</returns>
    public static string Generic(IReadOnlyList<AlertReport> reports, DateTimeOffset? sentAt = null)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var list = new JsonArray();
        foreach (var r in reports)
        {
            list.Add(new JsonObject
            {
                ["issueId"] = r.IssueId,
                ["itemId"] = r.ItemId?.ToString("N", CultureInfo.InvariantCulture),
                ["title"] = r.Title,
                ["reasonCode"] = r.ReasonCode,
                ["reason"] = r.Reason,
                ["user"] = r.User,
                ["device"] = r.Device,
                ["client"] = r.Client,
                ["playMethod"] = r.PlayMethod,
                ["transcodeReasons"] = new JsonArray(r.TranscodeReasons.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["howItPlayed"] = r.HowItPlayed,
                ["note"] = r.Note,
                ["filedAt"] = r.FiledAt.ToString("o", CultureInfo.InvariantCulture),
                ["link"] = r.Link,
            });
        }

        return new JsonObject
        {
            ["event"] = "reports.filed",
            ["sentAt"] = (sentAt ?? DateTimeOffset.UtcNow).ToString("o", CultureInfo.InvariantCulture),
            ["reports"] = list,
        }.ToJsonString(BodyOptions);
    }

    private AlertReport Describe(StoredReport report)
    {
        var reasons = _config().GetReasons();
        var transcode = report.TranscodeReasons?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return new AlertReport
        {
            IssueId = report.IssueId,
            ItemId = report.ItemId,
            Title = ReporterText.Title(report),
            ReasonCode = report.ReasonCode,
            Reason = reasons.FirstOrDefault(r => string.Equals(r.Code, report.ReasonCode, StringComparison.Ordinal))?.Label ?? report.ReasonCode,
            User = report.ReporterName,
            Device = report.Device,
            Client = report.Client,
            PlayMethod = report.PlayMethod,
            TranscodeReasons = transcode,
            HowItPlayed = HowItPlayed(report.PlayMethod, transcode),
            Note = report.Note is { Length: > MaxNoteLength } note ? note[..(MaxNoteLength - 1)].TrimEnd() + "…" : report.Note,
            FiledAt = report.CreatedAt,
            Link = PageLink(report.IssueId),
        };
    }

    private async Task Record(StoredReport report, AlertReport alert)
    {
        try
        {
            var who = alert.User ?? "A user";
            await _activity.CreateAsync(new ActivityLog($"{who} reported {alert.Title}: {alert.Reason}", ActivityType, report.ReporterUserId ?? Guid.Empty)
            {
                ItemId = report.ItemId.ToString("N", CultureInfo.InvariantCulture),
                ShortOverview = alert.Reason,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't add report {Report} to the activity log", report.Id);
        }
    }

    private async Task SendGroupLater()
    {
        await Task.Delay(Window, _time).ConfigureAwait(false);
        List<AlertReport> group;
        lock (_lock)
        {
            group = _waiting;
            _waiting = [];
        }

        try
        {
            await SendGroup(group).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't send the alert for {Count} report(s)", group.Count);
        }
    }

    private async Task SendGroup(List<AlertReport> group)
    {
        var config = _config();
        var sends = new List<Task>();
        if (!string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
        {
            sends.Add(SendWithRetry("Discord", config.DiscordWebhookUrl.Trim(), Discord(group)));
        }

        if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            sends.Add(SendWithRetry("generic", config.WebhookUrl.Trim(), Generic(group, _time.GetUtcNow())));
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task SendWithRetry(string channel, string address, string body)
    {
        var first = await Send(address, body).ConfigureAwait(false);
        if (first.Ok)
        {
            return;
        }

        _logger.LogWarning("The {Channel} webhook didn't take the report alert ({Problem}); trying once more in {Delay} seconds", channel, first.Problem, RetryDelay.TotalSeconds);
        await Task.Delay(RetryDelay, _time).ConfigureAwait(false);
        var second = await Send(address, body).ConfigureAwait(false);
        if (!second.Ok)
        {
            _logger.LogWarning("The {Channel} webhook didn't take the report alert again ({Problem}); it is dropped", channel, second.Problem);
        }
    }

    private async Task<(bool Ok, int? Status, string Problem)> Send(string address, string body)
    {
        using var timeout = new CancellationTokenSource(SendTimeout, _time);
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http().PostAsync(new Uri(address), content, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode ? (true, status, string.Empty) : (false, status, $"Answered {status}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return (false, null, $"No answer within {SendTimeout.TotalSeconds:0} seconds");
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
        {
            return (false, null, "Couldn't connect: " + ex.Message);
        }
    }

    private string PageLink(long? issueId)
    {
        var page = _serverUrl().TrimEnd('/') + "/web/#/configurationpage?name=" + Plugin.PageName;
        return issueId is null ? page : page + "&issue=" + issueId.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string HowItPlayed(string? playMethod, IReadOnlyList<string> transcodeReasons)
    {
        var method = playMethod switch
        {
            "DirectPlay" => "Direct play",
            "DirectStream" => "Direct stream",
            "Transcode" => "Transcode",
            null or "" => null,
            _ => playMethod,
        };
        if (method is null)
        {
            return "Not playing when reported";
        }

        return transcodeReasons.Count == 0 ? method : $"{method} ({string.Join(", ", transcodeReasons)})";
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static bool IsWebAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// One report as an alert describes it. It holds no file path, IP address or admin note.
/// </summary>
public sealed record AlertReport
{
    /// <summary>Gets the report's issue, or null for a sample.</summary>
    public long? IssueId { get; init; }

    /// <summary>Gets the reported item, or null for a sample.</summary>
    public Guid? ItemId { get; init; }

    /// <summary>Gets the title; an episode as "series S01E02".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the reason's code.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Gets the reason's label.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets the reporter's user name.</summary>
    public string? User { get; init; }

    /// <summary>Gets the device the report came with.</summary>
    public string? Device { get; init; }

    /// <summary>Gets the client the report came with.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the play method, as Jellyfin names it.</summary>
    public string? PlayMethod { get; init; }

    /// <summary>Gets why Jellyfin transcoded.</summary>
    public IReadOnlyList<string> TranscodeReasons { get; init; } = [];

    /// <summary>Gets how it played, in words.</summary>
    public string? HowItPlayed { get; init; }

    /// <summary>Gets the note, cut to 300 characters.</summary>
    public string? Note { get; init; }

    /// <summary>Gets when it was filed.</summary>
    public DateTimeOffset FiledAt { get; init; }

    /// <summary>Gets the link to the issue on the Media Reports page.</summary>
    public string Link { get; init; } = string.Empty;
}

/// <summary>
/// Whether one webhook took the test alert.
/// </summary>
/// <param name="Field">The setting: discordWebhookUrl or webhookUrl.</param>
/// <param name="Accepted">Whether it answered with a 2xx status.</param>
/// <param name="Status">The status it answered with, if it answered.</param>
/// <param name="Message">"Accepted", or what went wrong.</param>
public sealed record TestAlertResult(string Field, bool Accepted, int? Status, string Message);

/// <summary>
/// The addresses to send a test alert to, as typed in Settings.
/// </summary>
/// <param name="DiscordWebhookUrl">The Discord webhook address.</param>
/// <param name="WebhookUrl">The generic webhook address.</param>
public sealed record TestAlertRequest(string? DiscordWebhookUrl, string? WebhookUrl);
