// Packages the plugin for release and builds the Jellyfin plugin repository manifest.
// Runs as a .NET file-based app, so it needs only the .NET SDK (no zip, jq or python).
//
//   dotnet run scripts/release-tool.cs -- package --publish-dir <dir> --version <x.y.z.w>
//       --out <dir> --source-url-base <url> --changelog-file <CHANGELOG.md>
//     Writes <out>/media-reports_<version>.zip (meta.json stamped with the version and
//     its changelog entry) and <out>/media-reports_<version>.json, the manifest entry
//     for that zip. Fails when the changelog has no entry for the version.
//
//   dotnet run scripts/release-tool.cs -- changelog --file <CHANGELOG.md> --version <x.y.z[.w]> [--release]
//     Prints the version's entry: the text under its "## [x.y.z]" heading. A leading v
//     is ignored, and 0.1.0 and 0.1.0.0 name the same version. Fails without an entry.
//     With --release the heading must carry a release date ("## [0.1.0] - 2026-11-20"),
//     so an entry still marked Unreleased never becomes release notes.
//
//   dotnet run scripts/release-tool.cs -- manifest --meta <meta.json> --out <manifest.json>
//       --image-url-base <url> (--entries <dir> | --github)
//     Writes manifest.json from the entry files in <dir>, or from the entry file attached
//     to every GitHub Release of the repository (GITHUB_REPOSITORY, and GITHUB_TOKEN if set).
//     Copies the catalog image meta.json names next to it; the manifest gives its URL as
//     <url>/<image file name>.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

const string PackageName = "media-reports";

try
{
    if (args.Length == 0)
    {
        throw new ArgumentException("expected a command: package, manifest or changelog");
    }

    var options = ParseOptions(args[1..]);
    switch (args[0])
    {
        case "package":
            Package(options);
            break;
        case "manifest":
            await Manifest(options);
            break;
        case "changelog":
            Console.WriteLine(ChangelogEntry(Required(options, "file"), Required(options, "version"), options.ContainsKey("release")));
            break;
        default:
            throw new ArgumentException($"unknown command: {args[0]}");
    }

    return 0;
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or HttpRequestException or JsonException)
{
    Console.Error.WriteLine($"release-tool: {ex.Message}");
    return 1;
}

void Package(Dictionary<string, string> options)
{
    var publishDir = Required(options, "publish-dir");
    var version = Version.Parse(Required(options, "version"));
    if (version.Revision < 0)
    {
        throw new ArgumentException("--version needs four parts, e.g. 0.1.0.0");
    }

    var outDir = Required(options, "out");
    var sourceUrlBase = Required(options, "source-url-base").TrimEnd('/');
    var changelog = ChangelogEntry(Required(options, "changelog-file"), version.ToString());

    var meta = ReadJsonObject(Path.Combine(publishDir, "meta.json"));
    meta["version"] = version.ToString();
    meta["changelog"] = changelog;
    var targetAbi = meta["targetAbi"]?.GetValue<string>()
        ?? throw new InvalidDataException("meta.json has no targetAbi");

    Directory.CreateDirectory(outDir);
    var zipName = $"{PackageName}_{version}.zip";
    var zipPath = Path.Combine(outDir, zipName);
    File.Delete(zipPath);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        foreach (var file in Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories).Order())
        {
            var entryName = Path.GetRelativePath(publishDir, file).Replace('\\', '/');
            if (entryName == "meta.json")
            {
                continue;
            }

            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }

        using var metaStream = zip.CreateEntry("meta.json").Open();
        WriteJson(metaStream, meta);
    }

    string checksum;
    using (var stream = File.OpenRead(zipPath))
    {
        // Jellyfin verifies downloaded packages against an MD5 checksum.
        checksum = Convert.ToHexStringLower(MD5.HashData(stream));
    }

    var entry = new JsonObject
    {
        ["version"] = version.ToString(),
        ["changelog"] = changelog,
        ["targetAbi"] = targetAbi,
        ["sourceUrl"] = $"{sourceUrlBase}/{zipName}",
        ["checksum"] = checksum,
        ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };
    var entryPath = Path.Combine(outDir, $"{PackageName}_{version}.json");
    using (var entryStream = File.Create(entryPath))
    {
        WriteJson(entryStream, entry);
    }

    Console.WriteLine($"{zipPath} md5 {checksum}");
    Console.WriteLine(entryPath);
}

async Task Manifest(Dictionary<string, string> options)
{
    var metaPath = Required(options, "meta");
    var meta = ReadJsonObject(metaPath);
    var outPath = Required(options, "out");
    var imageUrlBase = Required(options, "image-url-base").TrimEnd('/');
    var imageName = meta["imagePath"]?.GetValue<string>()
        ?? throw new InvalidDataException("meta.json has no imagePath");

    List<JsonObject> entries;
    if (options.TryGetValue("entries", out var entriesDir))
    {
        entries = Directory.EnumerateFiles(entriesDir, $"{PackageName}_*.json")
            .Select(ReadJsonObject)
            .ToList();
    }
    else if (options.ContainsKey("github"))
    {
        entries = await ReadGitHubEntries();
    }
    else
    {
        throw new ArgumentException("manifest needs --entries <dir> or --github");
    }

    var versions = new JsonArray();
    foreach (var entry in entries.OrderByDescending(e => Version.Parse(e["version"]!.GetValue<string>())))
    {
        foreach (var field in new[] { "version", "targetAbi", "sourceUrl", "checksum", "timestamp" })
        {
            if (string.IsNullOrEmpty(entry[field]?.GetValue<string>()))
            {
                throw new InvalidDataException($"manifest entry {entry["version"]} has no {field}");
            }
        }

        versions.Add(entry.DeepClone());
    }

    var package = new JsonObject();
    foreach (var field in new[] { "guid", "name", "description", "overview", "owner", "category" })
    {
        package[field] = meta[field]?.DeepClone();
    }

    package["imageUrl"] = $"{imageUrlBase}/{imageName}";
    package["versions"] = versions;

    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
    Directory.CreateDirectory(dir);
    File.Copy(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(metaPath))!, imageName), Path.Combine(dir, imageName), true);
    using (var manifestStream = File.Create(outPath))
    {
        WriteJson(manifestStream, new JsonArray(package));
    }
    Console.WriteLine($"{outPath}: {versions.Count} version(s)");
}

async Task<List<JsonObject>> ReadGitHubEntries()
{
    var api = (Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com").TrimEnd('/');
    var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")
        ?? throw new ArgumentException("GITHUB_REPOSITORY is not set");
    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("media-reports-release-tool");
    var entries = new List<JsonObject>();
    for (var page = 1; ; page++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{api}/repos/{repository}/releases?per_page=100&page={page}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var releases = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        if (releases.Count == 0)
        {
            break;
        }

        foreach (var release in releases)
        {
            if (release!["draft"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var asset = release["assets"]?.AsArray()
                .FirstOrDefault(a => a!["name"]!.GetValue<string>() is var name
                    && name.StartsWith($"{PackageName}_", StringComparison.Ordinal)
                    && name.EndsWith(".json", StringComparison.Ordinal));
            if (asset is null)
            {
                Console.Error.WriteLine($"release-tool: skipping release {release["tag_name"]} with no manifest entry");
                continue;
            }

            // The download redirects to GitHub's asset storage; it needs no token for a public repository.
            var url = asset["browser_download_url"]!.GetValue<string>();
            entries.Add(JsonNode.Parse(await http.GetStringAsync(url))!.AsObject());
        }
    }

    return entries;
}

// The text under the "## [x.y.z]" heading of a Keep a Changelog file, up to the next
// "## " heading or the link definitions at the end.
static string ChangelogEntry(string path, string version, bool released = false)
{
    var wanted = FourParts(version) ?? throw new ArgumentException($"not a release version: {version}");
    var lines = File.ReadAllLines(path);
    var start = Array.FindIndex(lines, line => Regex.Match(line, @"^## \[([^\]]+)\]") is { Success: true } m
        && FourParts(m.Groups[1].Value) == wanted);
    if (start < 0)
    {
        throw new InvalidDataException($"{path} has no entry for {version}");
    }

    if (released && !Regex.IsMatch(lines[start], @"^## \[[^\]]+\] - \d{4}-\d{2}-\d{2}\s*$"))
    {
        throw new InvalidDataException($"{path}: the entry for {version} has no release date (\"{lines[start].Trim()}\")");
    }

    var body = lines.Skip(start + 1)
        .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal) && !Regex.IsMatch(line, @"^\[[^\]]+\]: "));
    var entry = string.Join('\n', body).Trim();
    return entry.Length > 0 ? entry : throw new InvalidDataException($"{path} has an empty entry for {version}");
}

static string? FourParts(string version)
{
    var parts = version.TrimStart('v').Split('.');
    if (parts.Length is < 3 or > 4 || parts.Any(p => !int.TryParse(p, out _)))
    {
        return null;
    }

    return string.Join('.', parts.Select(int.Parse).Concat(Enumerable.Repeat(0, 4 - parts.Length)));
}

// File-based apps disable reflection-based serialization, so nodes are written directly.
static void WriteJson(Stream stream, JsonNode node)
{
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    node.WriteTo(writer);
}

static JsonObject ReadJsonObject(string path)
    => JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"{path} is not a JSON object");

static string Required(Dictionary<string, string> options, string name)
    => options.TryGetValue(name, out var value) && value.Length > 0
        ? value
        : throw new ArgumentException($"--{name} is required");

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unexpected argument: {args[i]}");
        }

        var name = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        options[name] = hasValue ? args[++i] : string.Empty;
    }

    return options;
}
