using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Jellyfin.Plugin.MediaReports.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Playback;

/// <summary>
/// Serves the signed-in user's own live and recent playbacks.
/// </summary>
[ApiController]
[ApiErrors]
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
    public async Task<ActionResult<IReadOnlyList<PlaybackView>>> GetPlaybacks()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info.UserId.Equals(default))
        {
            return Forbid();
        }

        return Ok(_capture.GetPlaybacks(info.UserId).Select(Shape).ToList());
    }

    /// <summary>
    /// Gets a playback as the route answers it.
    /// </summary>
    /// <param name="entry">The captured playback.</param>
    /// <returns>The answer.</returns>
    internal static PlaybackView Shape(PlaybackEntry entry) => PlaybackView.From(entry);
}
