using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Notifications;

/// <summary>
/// Watches whether the Report button still loads: records web playback starts, and every hour
/// tells the webhooks when the button stops loading and when it loads again.
/// </summary>
public sealed class ButtonWatch : IHostedService, IDisposable
{
    /// <summary>
    /// How long without a load, while web clients play, before the button counts as stopped.
    /// </summary>
    public static readonly TimeSpan StoppedAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// How often the check runs.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// The plugin_state key holding whether the button counted as stopped at the last check.
    /// </summary>
    public const string StoppedKey = "button_stopped";

    /// <summary>
    /// The plugin_state key holding when reporting was last turned back on.
    /// </summary>
    public const string ReportingOnKey = "reporting_on_since";

    /// <summary>
    /// The plugin_state key set while the last check saw reporting turned off.
    /// </summary>
    public const string ReportingOffKey = "reporting_off";

    private readonly ISessionManager _sessions;
    private readonly ReportStore _store;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<bool, Task> _send;
    private readonly TimeProvider _time;
    private readonly ILogger<ButtonWatch> _logger;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="ButtonWatch"/> class.
    /// </summary>
    /// <param name="sessions">The session manager, for playback starts.</param>
    /// <param name="store">The report store.</param>
    /// <param name="config">Returns the current configuration.</param>
    /// <param name="send">Sends the stopped (true) or loading again (false) message to the webhooks.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ButtonWatch(ISessionManager sessions, ReportStore store, Func<PluginConfiguration> config, Func<bool, Task> send, TimeProvider time, ILogger<ButtonWatch> logger)
    {
        _sessions = sessions;
        _store = store;
        _config = config;
        _send = send;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a client name is an app built on jellyfin-web, where the Report button runs:
    /// Jellyfin Web, Jellyfin for Android and Jellyfin for webOS. Android TV, Roku and other
    /// native apps are not.
    /// </summary>
    /// <param name="client">The client name a session reports.</param>
    /// <returns>True for a jellyfin-web client.</returns>
    public static bool IsWebClient(string? client) =>
        client is not null
        && (string.Equals(client, "Jellyfin Web", StringComparison.OrdinalIgnoreCase)
            || string.Equals(client, "Jellyfin Android", StringComparison.OrdinalIgnoreCase)
            || client.Contains("webos", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Works out whether the Report button counts as stopped now: no load for 7 days, while
    /// web clients started playback in those days, after at least 7 days of watching since the
    /// first record or since reporting was turned back on. Never while reporting is off.
    /// </summary>
    /// <param name="store">The report store.</param>
    /// <param name="config">The configuration.</param>
    /// <param name="now">The time.</param>
    /// <returns>Whether it is stopped, and when it last loaded in any client.</returns>
    public static ButtonStatus Evaluate(ReportStore store, PluginConfiguration config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        var clients = store.GetClientsSeen();
        var lastLoad = clients.Max(c => c.LastLoad);
        if (!config.ReportingEnabled || clients.Count == 0)
        {
            return new ButtonStatus(false, lastLoad);
        }

        var watchingSince = clients.Min(c => c.FirstSeen);
        if (ParseTime(store.GetState(ReportingOnKey)) is { } on && on > watchingSince)
        {
            watchingSince = on;
        }

        var from = now - StoppedAfter;
        var stopped = watchingSince <= from
            && (lastLoad is null || lastLoad < from)
            && clients.Any(c => c.LastPlay >= from);
        return new ButtonStatus(stopped, lastLoad);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackStart;
        _stop = new CancellationTokenSource();
        _loop = RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        if (_stop is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the check once: keeps track of reporting being turned off and on, and sends one
    /// message when the button becomes stopped and one when it loads again.
    /// </summary>
    /// <returns>The status after the check.</returns>
    public async Task<ButtonStatus> CheckAsync()
    {
        await _checking.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = _config();
            var now = _time.GetUtcNow();
            if (!config.ReportingEnabled)
            {
                _store.SetState(ReportingOffKey, "1");
                _store.SetState(StoppedKey, null);
                return new ButtonStatus(false, null);
            }

            if (_store.GetState(ReportingOffKey) is not null)
            {
                _store.SetState(ReportingOnKey, now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
                _store.SetState(ReportingOffKey, null);
            }

            var status = Evaluate(_store, config, now);
            var was = _store.GetState(StoppedKey) is not null;
            if (status.Stopped != was)
            {
                _store.SetState(StoppedKey, status.Stopped ? "1" : null);
                _logger.LogInformation(status.Stopped ? "The Report button hasn't loaded in any app for 7 days" : "The Report button is loading again");
                await _send(status.Stopped).ConfigureAwait(false);
            }

            return status;
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop?.Dispose();
        _checking.Dispose();
    }

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private async Task RunAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                await CheckAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Couldn't check whether the Report button still loads");
            }
        }
        while (await WaitAsync(timer, stop).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stop)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (!IsWebClient(e.ClientName))
        {
            return;
        }

        try
        {
            _store.RecordPlayStart(e.ClientName, e.Session?.ApplicationVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't record a playback start in {Client}", e.ClientName);
        }
    }
}

/// <summary>
/// Whether the Report button counts as stopped, and when it last loaded in any client.
/// </summary>
/// <param name="Stopped">True when it counts as stopped.</param>
/// <param name="LastLoad">When it last loaded, or null when it never has.</param>
public sealed record ButtonStatus(bool Stopped, DateTimeOffset? LastLoad);
