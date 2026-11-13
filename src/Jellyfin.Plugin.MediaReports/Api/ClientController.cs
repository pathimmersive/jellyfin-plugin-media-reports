using Jellyfin.Plugin.MediaReports.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The Report button script's load signal, so the plugin notices when it stops loading.
/// </summary>
[ApiController]
[Authorize]
[Route("MediaReports/Client")]
public sealed class ClientController : ControllerBase
{
    /// <summary>
    /// The longest client name or version kept.
    /// </summary>
    public const int MaxLength = 100;

    private readonly ReportStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientController"/> class.
    /// </summary>
    /// <param name="store">The report store.</param>
    public ClientController(ReportStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Records that the Report button loaded in a client, at most once a minute per client.
    /// </summary>
    /// <param name="request">The client's name and version.</param>
    /// <returns>No content.</returns>
    [HttpPost("Loaded")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Loaded([FromBody] ClientLoaded request)
    {
        var client = request?.Client?.Trim();
        if (string.IsNullOrEmpty(client) || client.Length > MaxLength || request!.Version is { Length: > MaxLength })
        {
            return BadRequest(new ApiError("bad-client", $"The client name is required and, like the version, at most {MaxLength} characters."));
        }

        try
        {
            _store.RecordLoad(client, request.Version?.Trim());
        }
        catch (StorageUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError("storage-unavailable", "Media Reports storage is not available."));
        }

        return NoContent();
    }
}

/// <summary>
/// The Report button script's load signal.
/// </summary>
/// <param name="Client">The client name, as the app reports it.</param>
/// <param name="Version">The client version.</param>
public sealed record ClientLoaded(string? Client, string? Version);
