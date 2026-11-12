using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The Prometheus endpoint. Only an administrator's token or a Jellyfin API key reads it.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaReports/Metrics")]
public sealed class MetricsController : ControllerBase
{
    private readonly ReportMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsController"/> class.
    /// </summary>
    /// <param name="metrics">The plugin's counts.</param>
    public MetricsController(ReportMetrics metrics)
    {
        _metrics = metrics;
    }

    /// <summary>
    /// Gets the counts in the Prometheus text format. Answers even when the database didn't
    /// open, with media_reports_storage_up 0.
    /// </summary>
    /// <returns>The text.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ContentResult Get() => Content(_metrics.Render(), ReportMetrics.ContentType);
}
