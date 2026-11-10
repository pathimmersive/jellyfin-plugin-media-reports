using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Storage;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// Reads and saves the settings the Settings tab shows. A save is checked whole and
/// either saved whole or refused with nothing changed.
/// </summary>
public sealed class SettingsService
{
    /// <summary>
    /// The longest label a reason can have.
    /// </summary>
    public const int MaxLabelLength = 40;

    private readonly ReportStore _store;
    private readonly Func<PluginConfiguration> _config;
    private readonly Action<PluginConfiguration> _save;
    private readonly Func<string> _serverUrl;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="store">The report store, for report counts and the codes reports use.</param>
    /// <param name="config">Returns the current configuration; read on every call.</param>
    /// <param name="save">Saves a new configuration, which readers then see without a restart.</param>
    /// <param name="serverUrl">Returns the server's address, published when one is set, for the metrics address.</param>
    public SettingsService(ReportStore store, Func<PluginConfiguration> config, Action<PluginConfiguration> save, Func<string>? serverUrl = null)
    {
        _store = store;
        _config = config;
        _save = save;
        _serverUrl = serverUrl ?? (() => string.Empty);
    }

    /// <summary>
    /// Gets the settings, with each reason's report count.
    /// </summary>
    /// <returns>The settings.</returns>
    public AdminSettings Get() => Settings(_config(), _store.GetReasonReportCounts()) with
    {
        MetricsUrl = _serverUrl().TrimEnd('/') + "/MediaReports/Metrics",
    };

    /// <summary>
    /// Checks and saves a change to the settings.
    /// </summary>
    /// <param name="update">The change; a field left out keeps its saved value.</param>
    /// <returns>The saved settings, or what was refused.</returns>
    public (AdminSettings? Saved, SettingsError? Error) Update(SettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var current = _config();
        var next = Copy(current);

        if (update.Reasons is not null)
        {
            var (reasons, error) = Reasons(current.GetReasons(), update.Reasons);
            if (error is not null)
            {
                return (null, error);
            }

            next.Reasons = reasons!;
        }

        if (update.ReportRetentionDays is { } retention)
        {
            if (!TryDays(retention, out var days))
            {
                return (null, new SettingsError("retention-invalid", "Enter a whole number of days, 0 or more.", "reportRetentionDays"));
            }

            next.ReportRetentionDays = days;
        }

        foreach (var (value, field, set) in new (string?, string, Action<string>)[]
        {
            (update.DiscordWebhookUrl, "discordWebhookUrl", v => next.DiscordWebhookUrl = v),
            (update.WebhookUrl, "webhookUrl", v => next.WebhookUrl = v),
        })
        {
            if (value is null)
            {
                continue;
            }

            var address = value.Trim();
            if (address.Length > 0 && !IsWebAddress(address))
            {
                return (null, new SettingsError("webhook-invalid", "Enter an http or https address.", field));
            }

            set(address);
        }

        next.ReportingEnabled = update.ReportingEnabled ?? next.ReportingEnabled;
        next.SuspectFreezing = update.SuspectFreezing ?? next.SuspectFreezing;
        next.SuspectSlowTranscode = update.SuspectSlowTranscode ?? next.SuspectSlowTranscode;
        next.SuspectLowQuality = update.SuspectLowQuality ?? next.SuspectLowQuality;
        next.SuspectPlaybackError = update.SuspectPlaybackError ?? next.SuspectPlaybackError;

        _save(next);
        return (Get(), null);
    }

    /// <summary>
    /// Makes a code for a new reason from its label, different from every code taken.
    /// </summary>
    /// <param name="label">The reason's label.</param>
    /// <param name="taken">Codes in use, configured or stored.</param>
    /// <returns>The code.</returns>
    public static string NewCode(string label, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(taken);
        var slug = new StringBuilder();
        foreach (var c in label.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var stem = slug.ToString().Trim('-');
        stem = stem.Length == 0 ? "reason" : stem[..Math.Min(stem.Length, MaxLabelLength)].TrimEnd('-');
        var code = stem;
        for (var n = 2; taken.Contains(code); n++)
        {
            code = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}");
        }

        return code;
    }

    private (ReasonEntry[]? Reasons, SettingsError? Error) Reasons(ReasonEntry[] saved, IReadOnlyList<ReasonUpdate> wanted)
    {
        var known = saved.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var stored = _store.GetUsedReasonCodes();
        var named = wanted.Select(r => r.Code?.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToHashSet(StringComparer.Ordinal);

        // There is no delete: a saved reason the save leaves out is kept where it was last.
        var kept = saved.Where(r => !named.Contains(r.Code)).ToList();
        var result = new List<ReasonEntry>();
        var taken = new HashSet<string>(known.Concat(stored), StringComparer.Ordinal);
        if (Duplicate(saved, kept, wanted) is { } duplicate)
        {
            return (null, ReasonError("label-duplicate", "Another reason already has this name.", duplicate));
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var label = (wanted[i].Label ?? string.Empty).Trim();
            if (label.Length == 0)
            {
                return (null, ReasonError("label-empty", "Give the reason a name.", i));
            }

            if (label.Length > MaxLabelLength)
            {
                return (null, ReasonError("label-too-long", "Keep the name to 40 characters.", i));
            }

            var code = wanted[i].Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                code = NewCode(label, taken);
            }
            else if (result.Any(r => string.Equals(r.Code, code, StringComparison.Ordinal)))
            {
                return (null, ReasonError("code-duplicate", "This reason appears twice.", i));
            }
            else if (!known.Contains(code) && stored.Contains(code))
            {
                return (null, ReasonError("code-in-use", "Stored reports already use this reason's code.", i));
            }

            taken.Add(code);
            result.Add(new ReasonEntry { Code = code, Label = label, Retired = wanted[i].Retired });
        }

        result.AddRange(kept);
        if (result.All(r => r.Retired))
        {
            return (null, new SettingsError("no-active-reason", "Keep at least one reason users can pick.", "reasons"));
        }

        return (result.Select((r, i) => r with { Order = i }).ToArray(), null);
    }

    // Names the reason the admin edited or added when two share a label, ignoring case.
    private static int? Duplicate(ReasonEntry[] saved, List<ReasonEntry> kept, IReadOnlyList<ReasonUpdate> wanted)
    {
        var labels = wanted.Select(r => (r.Label ?? string.Empty).Trim()).ToList();
        bool Edited(int i) => !saved.Any(r => string.Equals(r.Code, wanted[i].Code?.Trim(), StringComparison.Ordinal) && r.Label == labels[i]);
        int? found = null;
        for (var i = 0; i < labels.Count; i++)
        {
            var clash = kept.Any(r => string.Equals(r.Label, labels[i], StringComparison.OrdinalIgnoreCase))
                || labels.Where((l, j) => j != i && l.Length > 0).Any(l => string.Equals(l, labels[i], StringComparison.OrdinalIgnoreCase));
            if (labels[i].Length > 0 && clash && (found is null || Edited(i)))
            {
                found = i;
            }
        }

        return found;
    }

    private static SettingsError ReasonError(string code, string message, int index) => new(code, message, "reasons", index);

    private static bool TryDays(JsonElement value, out int days)
    {
        days = 0;
        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out days),
            JsonValueKind.String => int.TryParse(value.GetString()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out days),
            _ => false,
        };
        return parsed && days >= 0;
    }

    private static bool IsWebAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static AdminSettings Settings(PluginConfiguration config, IReadOnlyDictionary<string, int> counts) => new()
    {
        ReportingEnabled = config.ReportingEnabled,
        Reasons = config.GetReasons().Select(r => new SettingsReason(r.Code, r.Label, r.Retired, counts.GetValueOrDefault(r.Code))).ToList(),
        ReportRetentionDays = Math.Max(0, config.ReportRetentionDays),
        SuspectFreezing = config.SuspectFreezing,
        SuspectSlowTranscode = config.SuspectSlowTranscode,
        SuspectLowQuality = config.SuspectLowQuality,
        SuspectPlaybackError = config.SuspectPlaybackError,
        DiscordWebhookUrl = config.DiscordWebhookUrl,
        WebhookUrl = config.WebhookUrl,
    };

    private static PluginConfiguration Copy(PluginConfiguration config) => new()
    {
        ReportingEnabled = config.ReportingEnabled,
        Reasons = config.GetReasons().Select(r => r with { }).ToArray(),
        SuspectFreezing = config.SuspectFreezing,
        SuspectSlowTranscode = config.SuspectSlowTranscode,
        SuspectLowQuality = config.SuspectLowQuality,
        SuspectPlaybackError = config.SuspectPlaybackError,
        DiscordWebhookUrl = config.DiscordWebhookUrl,
        WebhookUrl = config.WebhookUrl,
        PlaybackRetentionHours = config.PlaybackRetentionHours,
        ReportRetentionDays = config.ReportRetentionDays,
    };
}
