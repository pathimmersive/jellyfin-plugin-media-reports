using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.MediaReports.Api;

/// <summary>
/// Answers a request whose body or parameters can't be read with the plugin's error body,
/// 400 <c>bad-request</c>, before ASP.NET Core's own problem details are written. Every
/// Media Reports controller carries it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ApiErrorsAttribute : Attribute, IActionFilter, IOrderedFilter
{
    /// <summary>
    /// Gets the filter's order: before the invalid-model filter [ApiController] adds at -2000.
    /// </summary>
    public int Order => -3000;

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ModelState.IsValid)
        {
            return;
        }

        var field = context.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => e.Key.TrimStart('$', '.')).FirstOrDefault();
        var message = string.IsNullOrEmpty(field) ? "The request can't be read." : $"The request can't be read: {field}.";
        context.Result = new BadRequestObjectResult(new ApiError("bad-request", message));
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>
/// Gives an error body to a refusal under <c>/MediaReports</c> that has none: Jellyfin's own
/// 401 and 403, an unknown route's 404, and the like. Answers that already carry a body pass
/// through unchanged.
/// </summary>
public sealed class ApiErrorMiddleware
{
    private static readonly JsonSerializerOptions PascalCase = new();
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiErrorMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    public ApiErrorMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Gets a value indicating whether a path is one of the plugin's, under any BaseUrl.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>Whether the plugin answers it.</returns>
    public static bool IsPluginPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.Contains("/MediaReports/", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/MediaReports", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a value indicating whether a request asks for Jellyfin's camelCase JSON profile,
    /// as in <c>Accept: application/json; profile="CamelCase"</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>Whether field names should be camelCase.</returns>
    public static bool WantsCamelCase(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.Accept.Any(value => value is not null
            && value.Replace("\"", string.Empty, StringComparison.Ordinal).Contains("profile=camelcase", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the error code for an answer status.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>The code and a sentence.</returns>
    public static ApiError ErrorFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => new("bad-request", "The request can't be read."),
        StatusCodes.Status401Unauthorized => new("unauthorized", "Sign in to Jellyfin first."),
        StatusCodes.Status403Forbidden => new("forbidden", "This account can't do that."),
        StatusCodes.Status404NotFound => new("not-found", "There is nothing here."),
        StatusCodes.Status405MethodNotAllowed => new("method-not-allowed", "This address doesn't take that method."),
        StatusCodes.Status415UnsupportedMediaType => new("unsupported-media-type", "Send the body as JSON."),
        _ => new("error", "The request was refused."),
    };

    /// <summary>
    /// Serves the request, then writes the error body when a refusal has none.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _next(context).ConfigureAwait(false);
        var response = context.Response;
        if (response.StatusCode < 400 || response.StatusCode >= 500 || response.HasStarted
            || HttpMethods.IsHead(context.Request.Method) || !IsPluginPath(context.Request.Path))
        {
            return;
        }

        response.ContentType = "application/json; charset=utf-8";
        var options = WantsCamelCase(context.Request) ? CamelCase : PascalCase;
        await JsonSerializer.SerializeAsync(response.Body, ErrorFor(response.StatusCode), options, context.RequestAborted).ConfigureAwait(false);
    }
}
