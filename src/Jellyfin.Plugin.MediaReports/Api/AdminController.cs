using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// The admin's endpoints for working issues. Only Jellyfin administrators reach them.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaReports/Admin")]
public sealed class AdminController : ControllerBase
{
    private readonly AdminService _service;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="service">The admin's issue rules.</param>
    /// <param name="authorization">The request's authorization context.</param>
    public AdminController(AdminService service, IAuthorizationContext authorization)
    {
        _service = service;
        _authorization = authorization;
    }

    /// <summary>
    /// Counts open issues, their reports, and those new since the caller last looked.
    /// </summary>
    /// <param name="markSeen">True to record now as the caller's last look.</param>
    /// <returns>The summary.</returns>
    [HttpGet("Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AdminSummary>> GetSummary([FromQuery] bool markSeen = false)
    {
        var admin = await Caller().ConfigureAwait(false);
        return Answer(() => ServiceResult<AdminSummary>.Success(_service.GetSummary(admin, markSeen)), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Lists issues for the Reports tab, 50 rows a page.
    /// </summary>
    /// <param name="view">open, closed or all.</param>
    /// <param name="groupBy">title or titleClient.</param>
    /// <param name="reason">A reason code to show only.</param>
    /// <param name="client">A client to show only.</param>
    /// <param name="q">Title text to search for.</param>
    /// <param name="sort">title, reports, status or last.</param>
    /// <param name="page">The page, from 1.</param>
    /// <returns>The page of rows.</returns>
    [HttpGet("Issues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IssueList>> ListIssues(
        [FromQuery] string view = "open",
        [FromQuery] string groupBy = "title",
        [FromQuery] string? reason = null,
        [FromQuery] string? client = null,
        [FromQuery] string? q = null,
        [FromQuery] string sort = "last",
        [FromQuery] int page = 1)
    {
        var admin = await Caller().ConfigureAwait(false);
        var query = new IssueQuery { View = view, GroupBy = groupBy, Reason = reason, Client = client, Q = q, Sort = sort, Page = page };
        return Answer(() => _service.ListIssues(admin, query), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Opens one issue with its reports, history and the title as Jellyfin has it now.
    /// </summary>
    /// <param name="id">The issue.</param>
    /// <returns>The issue.</returns>
    [HttpGet("Issues/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IssueDetail> GetIssue([FromRoute] long id) =>
        Answer(() => _service.GetIssue(id), StatusCodes.Status200OK);

    /// <summary>
    /// Changes an issue's status, notes, or both.
    /// </summary>
    /// <param name="id">The issue.</param>
    /// <param name="patch">The change.</param>
    /// <returns>The updated issue.</returns>
    [HttpPatch("Issues/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IssueDetail>> UpdateIssue([FromRoute] long id, [FromBody] IssuePatch patch)
    {
        var admin = await Caller().ConfigureAwait(false);
        return Answer(() => _service.UpdateIssue(id, patch, admin), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Sets one status on several issues: all of them, or none.
    /// </summary>
    /// <param name="request">The issues and the status.</param>
    /// <returns>The issues set.</returns>
    [HttpPost("Issues/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<long>>> SetStatuses([FromBody] IssueStatusRequest request)
    {
        var admin = await Caller().ConfigureAwait(false);
        return Answer(() => _service.SetStatuses(request, admin), StatusCodes.Status200OK, many: true);
    }

    /// <summary>
    /// Queues Jellyfin's metadata refresh of the issue's title.
    /// </summary>
    /// <param name="id">The issue.</param>
    /// <returns>Accepted.</returns>
    [HttpPost("Issues/{id}/Refresh")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Refresh([FromRoute] long id)
    {
        var admin = await Caller().ConfigureAwait(false);
        return Answer(() => _service.Refresh(id, admin), StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Queues Jellyfin's scan of the folder holding the issue's title.
    /// </summary>
    /// <param name="id">The issue.</param>
    /// <returns>Accepted.</returns>
    [HttpPost("Issues/{id}/Scan")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Scan([FromRoute] long id)
    {
        var admin = await Caller().ConfigureAwait(false);
        return Answer(() => _service.Scan(id, admin), StatusCodes.Status202Accepted);
    }

    private async Task<Guid> Caller() =>
        (await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).UserId;

    // A refusal names the newer open issue for one issue, or every refused issue for several.
    private ObjectResult Answer<T>(Func<ServiceResult<T>> call, int successStatus, bool many = false)
    {
        ServiceResult<T> result;
        try
        {
            result = call();
        }
        catch (StorageUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new IssueError("storage-unavailable", "Reports can't be read right now."));
        }

        if (result.Outcome == Outcome.Ok)
        {
            return StatusCode(successStatus, result.Value);
        }

        var error = new IssueError(
            result.Code!,
            result.Message!,
            many ? null : result.IssueIds?.FirstOrDefault(),
            many ? result.IssueIds : null);
        return StatusCode(
            result.Outcome switch
            {
                Outcome.BadRequest => StatusCodes.Status400BadRequest,
                Outcome.NotFound => StatusCodes.Status404NotFound,
                Outcome.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status403Forbidden,
            },
            error);
    }
}
