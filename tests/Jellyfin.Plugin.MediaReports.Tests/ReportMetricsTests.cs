using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class ReportMetricsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly PluginConfiguration _config = new();
    private readonly ReportStore _store;
    private readonly ReportMetrics _metrics;

    public ReportMetricsTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        _store = new ReportStore(db, _clock, () => 365);
        _metrics = new ReportMetrics(_store, () => _config, "0.1.0.0");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Render_CountsReportsFiledByReasonAndClient_AndOpenReports()
    {
        var movie = Guid.NewGuid();
        File(movie, "stutter", "Jellyfin Web", "alice");
        File(movie, "stutter", "Jellyfin Web", "bob");
        File(Guid.NewGuid(), "audio", "Jellyfin Android TV", "carol");

        var text = _metrics.Render();

        Assert.Contains("media_reports_reports_filed_total{reason=\"stutter\",client=\"Jellyfin Web\"} 2\n", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_reports_filed_total{reason=\"audio\",client=\"Jellyfin Android TV\"} 1\n", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_open_reports 3\n", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE media_reports_reports_filed_total counter\n", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_storage_up 1\n", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_reporting_enabled 1\n", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_info{version=\"0.1.0.0\"} 1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CountsOpenIssuesByReasonAndStatus_LeavingClosedOnesOut()
    {
        var investigating = File(Guid.NewGuid(), "stutter", "Jellyfin Web", "alice");
        var fixedOne = File(Guid.NewGuid(), "audio", "Jellyfin Web", "alice");
        _store.SetStatus(investigating, IssueStatus.Investigating, null);
        _store.SetStatus(fixedOne, IssueStatus.Fixed, null);

        var text = _metrics.Render();

        Assert.Contains("media_reports_open_issues{reason=\"stutter\",status=\"Investigating\"} 1\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("media_reports_open_issues{reason=\"audio\"", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_open_reports 1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CarriesNoTitlePathUserOrDeviceName_AndEscapesLabels()
    {
        File(Guid.NewGuid(), "stutter", "Odd \"client\"\\name", "alice");

        var text = _metrics.Render();

        Assert.DoesNotContain("Secret Movie", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/media/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Living Room TV", text, StringComparison.Ordinal);
        Assert.Contains("client=\"Odd \\\"client\\\"\\\\name\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ReportsStorageDown_WithoutCounts_WhenTheDatabaseDidNotOpen()
    {
        var db = new ReportDatabase(Path.Combine(_dir, "never-opened.db"), NullLogger<ReportDatabase>.Instance);
        var metrics = new ReportMetrics(new ReportStore(db, _clock, () => 365), () => _config, "0.1.0.0");
        _config.ReportingEnabled = false;

        var text = metrics.Render();

        Assert.Contains("media_reports_storage_up 0\n", text, StringComparison.Ordinal);
        Assert.Contains("media_reports_reporting_enabled 0\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("media_reports_open_reports", text, StringComparison.Ordinal);
        Assert.DoesNotContain("media_reports_open_issues", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IsValidTextFormat_EveryLineAHelpTypeOrSample()
    {
        File(Guid.NewGuid(), "stutter", "Jellyfin Web", "alice");

        var lines = _metrics.Render().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, l => Assert.Matches(@"^(# (HELP|TYPE) media_reports_[a-z_]+ .+|media_reports_[a-z_]+(\{[a-z]+=""([^""\\]|\\.)*""(,[a-z]+=""([^""\\]|\\.)*"")*\})? -?[0-9]+)$", l));
    }

    private long File(Guid item, string reason, string client, string user)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var filed = _store.AddReport(new NewReport
        {
            ReporterUserId = Guid.NewGuid(),
            ReporterName = user,
            ItemId = item,
            ItemType = "Movie",
            ItemName = "Secret Movie",
            FilePath = "/media/movies/Secret Movie.mkv",
            ReasonCode = reason,
            Snapshot = new ReportSnapshot { Client = client, DeviceName = "Living Room TV", PlayMethod = "DirectPlay" },
        });
        _metrics.Filed(_store.GetReport(filed.ReportId)!);
        return filed.IssueId;
    }
}
