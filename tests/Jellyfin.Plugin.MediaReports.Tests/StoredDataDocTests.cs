using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaReports.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class ReadmeTests
{
    private const string Heading = "## What the plugin stores";

    [Fact]
    public void StoredDataSection_NamesEveryTableAndColumn()
    {
        var section = Section();
        var missing = SchemaNames().Where(name => !section.Contains($"`{name}`", StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0, $"README.md \"{Heading}\" does not name: {string.Join(", ", missing)}");
    }

    private static string Section()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
        var start = text.IndexOf(Heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"README.md has no \"{Heading}\" section");
        var end = text.IndexOf("\n## ", start + Heading.Length, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static List<string> SchemaNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"), "media-reports.db");
        var db = new ReportDatabase(path, NullLogger<ReportDatabase>.Instance);
        db.Initialize();
        Assert.True(db.IsAvailable);

        var names = new List<string>();
        using (var connection = db.OpenConnection())
        {
            var tables = Read(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
            foreach (var table in tables)
            {
                names.Add(table);
                names.AddRange(Read(connection, $"SELECT name FROM pragma_table_info('{table}')"));
            }
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        return names.Distinct().ToList();
    }

    private static List<string> Read(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
