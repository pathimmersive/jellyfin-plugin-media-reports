using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public class PluginTests
{
    private const string ExpectedId = "3a96edc1-4775-492e-ba6c-c0d8abc88662";

    [Fact]
    public void Plugin_DeclaresNameAndFixedId()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(Path.Combine(root, "plugins"));
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(root, "configurations"));

        var plugin = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());

        Assert.Equal("Media Reports", plugin.Name);
        Assert.Equal(Guid.Parse(ExpectedId), plugin.Id);
    }

    [Fact]
    public void Pages_ListMediaReportsUnderPlugins_WithItsScript_BothEmbedded()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(Path.Combine(root, "plugins"));
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(root, "configurations"));

        var pages = new Plugin(paths.Object, Mock.Of<IXmlSerializer>()).GetPages().ToList();

        var page = Assert.Single(pages, p => p.EnableInMainMenu);
        Assert.Equal(("mediareports", "Media Reports", "report_problem"), (page.Name, page.DisplayName, page.MenuIcon));
        Assert.Contains(pages, p => p.Name == "mediareports.js");
        foreach (var resource in pages.Select(p => p.EmbeddedResourcePath))
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resource);
            Assert.True(stream is { Length: > 0 }, resource);
        }

        using var html = new StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath)!);
        Assert.Contains("data-controller=\"__plugin/mediareports.js\"", html.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_MatchesPluginAndTargetsAbi12()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "meta.json")));
        var root = meta.RootElement;

        Assert.Equal("Media Reports", root.GetProperty("name").GetString());
        Assert.Equal(Guid.Parse(ExpectedId), Guid.Parse(root.GetProperty("guid").GetString()!));
        Assert.Equal("12.0.0.0", root.GetProperty("targetAbi").GetString());
    }
}
