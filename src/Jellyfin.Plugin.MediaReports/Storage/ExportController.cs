using System;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// Serves the admin's download of every stored report.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaReports/Admin")]
public sealed class ExportController : ControllerBase
{
    private readonly ReportStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportController"/> class.
    /// </summary>
    /// <param name="store">The report store.</param>
    public ExportController(ReportStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Downloads every report, issue and event as one JSON document.
    /// </summary>
    /// <returns>The JSON file.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Export()
    {
        var output = new MemoryStream();
        try
        {
            _store.Export(output);
        }
        catch (StorageUnavailableException)
        {
            output.Dispose();
            return Problem(title: "storage-unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        output.Position = 0;
        var name = string.Create(CultureInfo.InvariantCulture, $"media-reports-export-{DateTime.UtcNow:yyyy-MM-dd}.json");
        return File(output, "application/json", name);
    }
}
