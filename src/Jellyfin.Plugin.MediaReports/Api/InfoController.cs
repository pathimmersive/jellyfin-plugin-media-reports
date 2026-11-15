using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// Tells a client the plugin is installed: its version, the API version and whether reporting is on.
/// </summary>
[ApiController]
[ApiErrors]
[Authorize]
[Route("MediaReports/Info")]
public sealed class InfoController : ControllerBase
{
    /// <summary>
    /// The API version, raised only by a change that would break a client.
    /// </summary>
    public const int ApiVersion = 1;

    /// <summary>
    /// Gets the plugin's version, the API version, whether reporting is on and the item types a
    /// report can be filed on.
    /// </summary>
    /// <returns>The information.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginInfo> Get() => new PluginInfo(
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
        ApiVersion,
        Plugin.Instance?.Configuration?.ReportingEnabled ?? true,
        ReportService.ReportableTypes);
}

/// <summary>
/// What a client needs to know about the installed plugin.
/// </summary>
/// <param name="Version">The plugin's version.</param>
/// <param name="ApiVersion">The API version.</param>
/// <param name="ReportingEnabled">Whether reporting is turned on.</param>
/// <param name="ReportableTypes">The item types a report can be filed on.</param>
public sealed record PluginInfo(string Version, int ApiVersion, bool ReportingEnabled, IReadOnlyList<string> ReportableTypes);
