using System;
using System.IO;
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
    public void Metadata_MatchesPluginAndTargetsAbi12()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "meta.json")));
        var root = meta.RootElement;

        Assert.Equal("Media Reports", root.GetProperty("name").GetString());
        Assert.Equal(Guid.Parse(ExpectedId), Guid.Parse(root.GetProperty("guid").GetString()!));
        Assert.Equal("12.0.0.0", root.GetProperty("targetAbi").GetString());
    }
}
