using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public class IndexPageMiddlewareTests
{
    private const string Index = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body></body></html>";
    private const string StockTag = "\"stock\"";

    private static readonly string Tag = "<script src=\"../MediaReports/web/reporter.js?v=" + typeof(Plugin).Assembly.GetName().Version + "\"></script>";

    // Stands in for Jellyfin's static files: honours the validators, compression and
    // HEAD the way the real pipeline does, so the middleware has to undo each one.
    private static Task StaticFiles(HttpContext context, string page)
    {
        var request = context.Request;
        var response = context.Response;
        response.ContentType = "text/html";
        response.Headers.ETag = StockTag;
        response.Headers.LastModified = "Wed, 01 Jan 2026 00:00:00 GMT";
        if (request.Headers.IfNoneMatch == StockTag || request.Headers.ContainsKey(HeaderNames.IfModifiedSince))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        var bytes = Encoding.UTF8.GetBytes(page);
        if (request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.Ordinal))
        {
            using var zipped = new MemoryStream();
            using (var gzip = new GZipStream(zipped, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(bytes);
            }

            bytes = zipped.ToArray();
            response.Headers.ContentEncoding = "gzip";
        }

        response.ContentLength = bytes.Length;
        return HttpMethods.IsHead(request.Method) ? Task.CompletedTask : response.Body.WriteAsync(bytes).AsTask();
    }

    private static async Task<(HttpContext Context, string Body)> Serve(
        string path,
        string method = "GET",
        Action<HttpRequest>? setUp = null,
        RequestDelegate? next = null,
        string page = Index,
        Func<string, string?>? change = null,
        bool reporting = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        setUp?.Invoke(context.Request);
        var body = new MemoryStream();
        context.Response.Body = body;

        await new IndexPageMiddleware(next ?? (c => StaticFiles(c, page)), NullLogger<IndexPageMiddleware>.Instance, () => reporting, change ?? IndexPage.AddScript).InvokeAsync(context);

        return (context, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Theory]
    [InlineData("/web/index.html")]
    [InlineData("/web/")]
    [InlineData("/web")]
    [InlineData("/jf/web/index.html")]
    [InlineData("/jf/web/")]
    public async Task TheIndexPageGetsOneTagUnderAnyBaseUrl(string path)
    {
        var (context, body) = await Serve(path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(Index.Replace("</head>", Tag + "</head>", StringComparison.Ordinal), body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Response.ContentLength);
        Assert.Equal(IndexPageMiddleware.ETagOf(Encoding.UTF8.GetBytes(body)), context.Response.Headers.ETag.ToString());
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.LastModified));
    }

    [Fact]
    public async Task ACompressedRequestGetsTheChangedPageUncompressed()
    {
        var (context, body) = await Serve("/web/index.html", setUp: r => r.Headers.AcceptEncoding = "gzip, br");

        Assert.Contains(Tag, body, StringComparison.Ordinal);
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding));
    }

    [Fact]
    public async Task HeadGetsTheSameHeadersAsGetAndNoBody()
    {
        var (get, getBody) = await Serve("/web/index.html");
        var (head, headBody) = await Serve("/web/index.html", "HEAD");

        Assert.Equal(string.Empty, headBody);
        Assert.Equal("HEAD", head.Request.Method);
        Assert.Equal(get.Response.ContentLength, head.Response.ContentLength);
        Assert.Equal(get.Response.Headers.ETag.ToString(), head.Response.Headers.ETag.ToString());
        Assert.NotEmpty(getBody);
    }

    [Fact]
    public async Task AMatchingETagGets304_AndTheStockValidatorsAreNotPassedOn()
    {
        var (first, body) = await Serve("/web/index.html");
        var etag = first.Response.Headers.ETag.ToString();

        var (again, againBody) = await Serve("/web/index.html", setUp: r =>
        {
            r.Headers.IfNoneMatch = etag;
            r.Headers.IfModifiedSince = "Wed, 01 Jan 2026 00:00:00 GMT";
        });
        var (stale, staleBody) = await Serve("/web/index.html", setUp: r => r.Headers.IfNoneMatch = StockTag);

        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);
        Assert.Equal(string.Empty, againBody);
        Assert.Equal(StatusCodes.Status200OK, stale.Response.StatusCode);
        Assert.Equal(body, staleBody);
    }

    [Fact]
    public async Task APageThatAlreadyHasTheTag_OrOtherPluginScripts_GetsExactlyOne()
    {
        var withOthers = Index.Replace("</head>", "<script src=\"/FileTransformation/a.js\"></script><script defer src=\"main.js\"></script></head>", StringComparison.Ordinal);
        var (_, once) = await Serve("/web/index.html", page: withOthers);
        var (_, twice) = await Serve("/web/index.html", page: once);

        Assert.Equal(1, Count(once, "reporter.js"));
        Assert.Equal(once, twice);
        Assert.Contains("/FileTransformation/a.js", once, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageWithoutAHeadIsLeftAlone()
    {
        const string NoHead = "<html><body>maintenance</body></html>";

        var (_, body) = await Serve("/web/index.html", page: NoHead);

        Assert.Equal(NoHead, body);
    }

    [Fact]
    public async Task ReportingOff_ServesTheStockPage()
    {
        var (context, body) = await Serve("/web/index.html", reporting: false);

        Assert.Equal(Index, body);
        Assert.Equal(StockTag, context.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task AFailingChange_ServesTheStockPage()
    {
        var (context, body) = await Serve("/web/index.html", change: _ => throw new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(Index, body);
    }

    [Theory]
    [InlineData("/web/main.js", "GET")]
    [InlineData("/Users/Me", "GET")]
    [InlineData("/web/index.html", "POST")]
    public async Task OtherRequestsPassThroughUnbuffered(string path, string method)
    {
        Stream? seen = null;
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        var original = new MemoryStream();
        context.Response.Body = original;

        await new IndexPageMiddleware(c => { seen = c.Response.Body; return Task.CompletedTask; }, NullLogger<IndexPageMiddleware>.Instance, () => true, IndexPage.AddScript).InvokeAsync(context);

        Assert.Same(original, seen);
    }

    [Fact]
    public async Task AnErrorPageIsNotChanged()
    {
        var (context, body) = await Serve("/web/index.html", next: async c =>
        {
            c.Response.StatusCode = StatusCodes.Status404NotFound;
            await c.Response.WriteAsync("<html><head></head></html>");
        });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.DoesNotContain("reporter.js", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptIsEmbedded_AndItsUrlCarriesThePluginVersion()
    {
        using var script = new ReporterScript().Open();
        using var reader = new StreamReader(script);

        Assert.Contains("Report a problem", reader.ReadToEnd(), StringComparison.Ordinal);
        Assert.Contains("?v=" + typeof(Plugin).Assembly.GetName().Version, Tag, StringComparison.Ordinal);
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
