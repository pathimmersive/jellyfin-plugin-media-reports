using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Web;

/// <summary>
/// The one change made to jellyfin-web as it is served: a script tag for the Report button in index.html.
/// </summary>
public static class IndexPage
{
    // Relative to {base}/web/, so it resolves under any configured BaseUrl.
    private const string ScriptSource = "../MediaReports/web/" + ReporterScript.FileName;

    // The version changes the script's URL on every upgrade, so no client keeps running an old copy.
    private static readonly string _scriptTag = "<script src=\"" + ScriptSource + "?v=" + ReporterScript.Version + "\"></script>";

    /// <summary>
    /// Adds the Report button's script to index.html as a classic script.
    /// </summary>
    /// <param name="html">The served index.html.</param>
    /// <returns>The changed page, or null to leave it unchanged.</returns>
    public static string? AddScript(string html)
    {
        if (html.Contains("<script src=\"" + ScriptSource, StringComparison.Ordinal))
        {
            return null;
        }

        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? null : html.Insert(head, _scriptTag);
    }
}

/// <summary>
/// Puts <see cref="IndexPageMiddleware"/> ahead of the rest of Jellyfin's pipeline,
/// so it sees jellyfin-web's index page before it is sent.
/// </summary>
public sealed partial class IndexPageStartupFilter : IStartupFilter
{
    private readonly ILogger<IndexPageStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexPageStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public IndexPageStartupFilter(ILogger<IndexPageStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        LogAdded(_logger);
        app.UseMiddleware<IndexPageMiddleware>();
        next(app);
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Report button added to jellyfin-web")]
    private static partial void LogAdded(ILogger logger);
}
