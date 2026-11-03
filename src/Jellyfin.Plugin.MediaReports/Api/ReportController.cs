using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The signed-in user's endpoints for filing a report. Every call acts as the caller; none takes a user id.
/// </summary>
[ApiController]
[Authorize]
[Route("MediaReports/Report")]
public sealed class ReportController : ControllerBase
{
    private readonly ReportService _service;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportController"/> class.
    /// </summary>
    /// <param name="service">The report rules.</param>
    /// <param name="authorization">The request's authorization context.</param>
    public ReportController(ReportService service, IAuthorizationContext authorization)
    {
        _service = service;
        _authorization = authorization;
    }

    /// <summary>
    /// Describes what the caller is about to report: the title, the reasons on offer, their
    /// open reports on it, and the playback that would be attached.
    /// </summary>
    /// <param name="itemId">The movie, episode, season or series.</param>
    /// <returns>The context.</returns>
    [HttpGet("Context")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReportContext>> GetContext([FromQuery] Guid itemId)
    {
        var (userId, deviceId) = await Caller().ConfigureAwait(false);
        if (userId is null)
        {
            return Forbid();
        }

        return Answer(() => _service.GetContext(userId.Value, deviceId, itemId), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Files a report. The server picks and snapshots the playback to attach.
    /// </summary>
    /// <param name="request">The report.</param>
    /// <returns>The filed report.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FiledReportView>> File([FromBody] FileReportRequest request)
    {
        var (userId, deviceId) = await Caller().ConfigureAwait(false);
        if (userId is null)
        {
            return Forbid();
        }

        return Answer(() => _service.File(userId.Value, deviceId, request), StatusCodes.Status201Created);
    }

    /// <summary>
    /// Lists the caller's reports, newest first, with how many have an unread status change.
    /// </summary>
    /// <returns>The reports.</returns>
    [HttpGet("Mine")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MyReports>> GetMine()
    {
        var (userId, _) = await Caller().ConfigureAwait(false);
        if (userId is null)
        {
            return Forbid();
        }

        return Answer(() => _service.Mine(userId.Value), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Opens one of the caller's reports and marks its status as seen.
    /// </summary>
    /// <param name="id">The report.</param>
    /// <returns>The report.</returns>
    [HttpGet("Mine/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MyReportDetail>> GetMineDetail([FromRoute] long id)
    {
        var (userId, _) = await Caller().ConfigureAwait(false);
        if (userId is null)
        {
            return Forbid();
        }

        return Answer(() => _service.MineDetail(userId.Value, id), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Withdraws one of the caller's reports while it is New.
    /// </summary>
    /// <param name="id">The report.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Mine/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Withdraw([FromRoute] long id)
    {
        var (userId, _) = await Caller().ConfigureAwait(false);
        if (userId is null)
        {
            return Forbid();
        }

        var answer = Answer(() => _service.Withdraw(userId.Value, id), StatusCodes.Status204NoContent);
        return answer.StatusCode == StatusCodes.Status204NoContent ? NoContent() : answer;
    }

    /// <summary>
    /// Removes one of the caller's closed reports from their list.
    /// </summary>
    /// <param name="id">The report.</param>
    /// <returns>No content.</returns>
    [HttpPost("Mine/{id}/Hide")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Hide([FromRoute] long id)
    {
        var (userId, _) = await Caller().ConfigureAwait(false);
        if (userId is null)
        {
            return Forbid();
        }

        var answer = Answer(() => _service.Hide(userId.Value, id), StatusCodes.Status204NoContent);
        return answer.StatusCode == StatusCodes.Status204NoContent ? NoContent() : answer;
    }

    /// <summary>
    /// Removes every closed report from the caller's list.
    /// </summary>
    /// <returns>How many were removed.</returns>
    [HttpPost("Mine/HideClosed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<HiddenReports>> HideClosed()
    {
        var (userId, _) = await Caller().ConfigureAwait(false);
        if (userId is null)
        {
            return Forbid();
        }

        return Answer(() => _service.HideClosed(userId.Value), StatusCodes.Status200OK);
    }

    private async Task<(Guid? UserId, string? DeviceId)> Caller()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info.UserId.Equals(default) ? (null, null) : (info.UserId, info.DeviceId);
    }

    private ObjectResult Answer<T>(Func<ServiceResult<T>> call, int successStatus)
    {
        ServiceResult<T> result;
        try
        {
            result = call();
        }
        catch (StorageUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError("storage-unavailable", "Reports can't be stored right now."));
        }

        return result.Outcome switch
        {
            Outcome.Ok => StatusCode(successStatus, result.Value),
            Outcome.BadRequest => StatusCode(StatusCodes.Status400BadRequest, new ApiError(result.Code!, result.Message!)),
            Outcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new ApiError(result.Code!, result.Message!)),
            Outcome.NotFound => StatusCode(StatusCodes.Status404NotFound, new ApiError(result.Code!, result.Message!)),
            _ => StatusCode(StatusCodes.Status409Conflict, new ApiError(result.Code!, result.Message!)),
        };
    }
}
