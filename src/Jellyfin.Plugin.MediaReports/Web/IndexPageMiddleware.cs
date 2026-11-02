using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.MediaReports.Web;

/// <summary>
/// Adds the Report button's script tag to jellyfin-web's index page as it is served.
/// Every other request passes through untouched; the file on disk is never written.
/// </summary>
public sealed partial class IndexPageMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IndexPageMiddleware> _logger;
    private readonly Func<bool> _reportingEnabled;
    private readonly Func<string, string?> _change;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexPageMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="logger">The logger.</param>
    public IndexPageMiddleware(RequestDelegate next, ILogger<IndexPageMiddleware> logger)
        : this(next, logger, () => Plugin.Instance?.Configuration.ReportingEnabled ?? false, IndexPage.AddScript)
    {
    }

    // The switch and the change are parameters so the tests can set and break them.
    internal IndexPageMiddleware(RequestDelegate next, ILogger<IndexPageMiddleware> logger, Func<bool> reportingEnabled, Func<string, string?> change)
    {
        _next = next;
        _logger = logger;
        _reportingEnabled = reportingEnabled;
        _change = change;
    }

    /// <summary>
    /// Gets a value indicating whether a path is jellyfin-web's index page, under any BaseUrl.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>Whether the page is changed on its way out.</returns>
    public static bool IsIndexPage(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the entity tag of a page: a quoted prefix of its SHA-256.
    /// </summary>
    /// <param name="page">The page as sent.</param>
    /// <returns>The entity tag.</returns>
    public static string ETagOf(byte[] page) => "\"" + Convert.ToHexStringLower(SHA256.HashData(page))[..16] + "\"";

    /// <summary>
    /// Serves the request, changing the index page while reporting is on.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var head = HttpMethods.IsHead(request.Method);
        if (!(head || HttpMethods.IsGet(request.Method)) || !IsIndexPage(request.Path) || !_reportingEnabled())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // The stock page is asked for whole, uncompressed and unconditional: the client's
        // validators describe the changed page, which only this middleware can answer for.
        var clientTag = request.Headers.IfNoneMatch.ToString();
        foreach (var name in new[] { HeaderNames.AcceptEncoding, HeaderNames.IfNoneMatch, HeaderNames.IfModifiedSince, HeaderNames.Range, HeaderNames.IfRange })
        {
            request.Headers.Remove(name);
        }

        // A HEAD runs as a GET, so its headers describe the page a GET would get.
        if (head)
        {
            request.Method = HttpMethods.Get;
        }

        var response = context.Response;
        var client = response.Body;
        byte[] stock;
        using (var buffer = new MemoryStream())
        {
            response.Body = buffer;
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                response.Body = client;
                if (head)
                {
                    request.Method = HttpMethods.Head;
                }
            }

            stock = buffer.ToArray();
        }

        var page = stock;
        if (response.StatusCode == StatusCodes.Status200OK && !response.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            page = Change(stock);
            response.Headers.Remove(HeaderNames.LastModified);
            response.Headers.ETag = ETagOf(page);
            if (clientTag.Split(',').Select(tag => tag.Trim()).Any(tag => tag == response.Headers.ETag.ToString() || tag == "*"))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentLength = null;
                return;
            }
        }

        response.ContentLength = page.Length;
        if (!head)
        {
            await client.WriteAsync(page).ConfigureAwait(false);
        }
    }

    private byte[] Change(byte[] stock)
    {
        try
        {
            var changed = _change(Encoding.UTF8.GetString(stock));
            return changed is null ? stock : Encoding.UTF8.GetBytes(changed);
        }
        catch (Exception exception)
        {
            // A fault can only cost the Report button, never jellyfin-web.
            LogChangeFailed(_logger, exception);
            return stock;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Adding the Report button to jellyfin-web failed; serving the page unchanged")]
    private static partial void LogChangeFailed(ILogger logger, Exception exception);
}
