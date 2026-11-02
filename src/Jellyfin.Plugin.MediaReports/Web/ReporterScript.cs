using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaReports.Web;

/// <summary>
/// The Report button script, embedded in the plugin assembly.
/// </summary>
public class ReporterScript
{
    /// <summary>
    /// The script's file name, under the plugin's web route.
    /// </summary>
    public const string FileName = "reporter.js";

    /// <summary>
    /// The script's content type.
    /// </summary>
    public const string ContentType = "text/javascript; charset=utf-8";

    private static readonly Lazy<string> _etag = new(ComputeETag);

    /// <summary>
    /// Gets the plugin version, which the script tag carries so an upgrade changes the script's URL.
    /// </summary>
    public static string Version { get; } = typeof(ReporterScript).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>
    /// Gets the entity tag of the embedded script: a quoted prefix of its SHA-256.
    /// </summary>
    public string ETag => _etag.Value;

    /// <summary>
    /// Opens the script.
    /// </summary>
    /// <returns>The script content.</returns>
    public Stream Open() =>
        typeof(ReporterScript).Assembly.GetManifestResourceStream(FileName)
        ?? throw new FileNotFoundException("The Report button script is not embedded in the plugin assembly.", FileName);

    private static string ComputeETag()
    {
        using var stream = new ReporterScript().Open();
        return "\"" + Convert.ToHexStringLower(SHA256.HashData(stream))[..16] + "\"";
    }
}

/// <summary>
/// Serves the Report button script that jellyfin-web loads.
/// </summary>
[ApiController]
[Route("MediaReports/web")]
public sealed class ReporterScriptController : ControllerBase
{
    private readonly ReporterScript _script = new();

    /// <summary>
    /// Serves the script. Anonymous, because a script tag can't send an Authorization header;
    /// the script holds no data of its own. Clients revalidate it on every load.
    /// </summary>
    /// <returns>The script, or 304 when the client already has it.</returns>
    [HttpGet(ReporterScript.FileName)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public ActionResult GetScript()
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.ETag = _script.ETag;
        var clientHas = Request.Headers.IfNoneMatch
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
            .Any(candidate => candidate == _script.ETag || candidate == "*");
        return clientHas ? StatusCode(StatusCodes.Status304NotModified) : File(_script.Open(), ReporterScript.ContentType);
    }
}
