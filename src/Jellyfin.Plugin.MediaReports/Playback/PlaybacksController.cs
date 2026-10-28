using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Playback;

/// <summary>
/// Serves the signed-in user's own live and recent playbacks.
/// </summary>
[ApiController]
[Authorize]
[Route("MediaReports")]
public sealed class PlaybacksController : ControllerBase
{
    private readonly PlaybackCapture _capture;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybacksController"/> class.
    /// </summary>
    /// <param name="capture">The playback capture.</param>
    /// <param name="authorization">The request's authorization context.</param>
    public PlaybacksController(PlaybackCapture capture, IAuthorizationContext authorization)
    {
        _capture = capture;
        _authorization = authorization;
    }

    /// <summary>
    /// Gets the caller's live and recent playbacks, newest first.
    /// </summary>
    /// <returns>The playbacks.</returns>
    [HttpGet("Playbacks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PlaybackEntry>>> GetPlaybacks()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info.UserId.Equals(default))
        {
            return Forbid();
        }

        return Ok(_capture.GetPlaybacks(info.UserId));
    }
}
