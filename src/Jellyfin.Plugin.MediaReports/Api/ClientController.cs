using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Net;
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
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientController"/> class.
    /// </summary>
    /// <param name="store">The report store.</param>
    /// <param name="authorization">The request's authorization context.</param>
    public ClientController(ReportStore store, IAuthorizationContext authorization)
    {
        _store = store;
        _authorization = authorization;
    }

    /// <summary>
    /// Records that the Report button loaded in the caller's app, at most once a minute per app.
    /// The app is the client named in the caller's Jellyfin session; a body is ignored.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Loaded")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Loaded()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        var client = info.Client?.Trim();
        var version = info.Version?.Trim();
        if (string.IsNullOrEmpty(client) || client.Length > MaxLength || version is { Length: > MaxLength })
        {
            return BadRequest(new ApiError("bad-client", $"The session names no client, or a client or version over {MaxLength} characters."));
        }

        try
        {
            _store.RecordLoad(client, string.IsNullOrEmpty(version) ? null : version);
        }
        catch (StorageUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError("storage-unavailable", "Media Reports storage is not available."));
        }

        return NoContent();
    }
}

