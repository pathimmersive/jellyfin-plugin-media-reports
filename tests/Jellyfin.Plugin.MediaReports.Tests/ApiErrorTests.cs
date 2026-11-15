using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Api;
using Jellyfin.Plugin.MediaReports.Configuration;
using Jellyfin.Plugin.MediaReports.Playback;
using Jellyfin.Plugin.MediaReports.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

// Runs the plugin's controllers in a real ASP.NET Core pipeline on the loopback address, with
// a test sign-in standing in for Jellyfin's: "X-Test-User" signs in, "X-Test-Admin" adds the admin role.
public sealed class ApiErrorTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-reports-tests", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ReportController).Assembly);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestSignIn>("Test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(Policies.RequiresElevation, p => p.RequireRole("Administrator")));

        var store = new ReportStore(new ReportDatabase(Path.Combine(_dir, "media-reports.db"), NullLogger<ReportDatabase>.Instance), TimeProvider.System, () => 365);
        var sessions = new Mock<ISessionManager>();
        var library = new Mock<ILibraryManager>();
        builder.Services.AddSingleton(new ReportService(
            library.Object,
            new Mock<IUserManager>().Object,
            new PlaybackCapture(sessions.Object, library.Object, TimeProvider.System, () => TimeSpan.FromHours(1)),
            store,
            () => new PluginConfiguration(),
            TimeProvider.System));
        builder.Services.AddSingleton(new Mock<IAuthorizationContext>().Object);

        _app = builder.Build();
        _app.UseMiddleware<ApiErrorMiddleware>();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ABodyThatCantBeRead_IsRefusedWithTheErrorBody_NotProblemDetails()
    {
        var (status, body) = await Send(HttpMethod.Post, "/MediaReports/Report", user: true, json: "{ \"itemId\": ");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad-request", Field(body, "Code"));
        Assert.False(string.IsNullOrEmpty(Field(body, "Message")));
        Assert.False(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task ARouteValueThatCantBeRead_IsRefusedWithTheErrorBody()
    {
        var (status, body) = await Send(HttpMethod.Get, "/MediaReports/Report/Mine/not-a-number", user: true);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad-request", Field(body, "Code"));
    }

    [Theory]
    [InlineData("GET", "/MediaReports/Info", false, HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData("GET", "/MediaReports/Admin/Summary", true, HttpStatusCode.Forbidden, "forbidden")]
    [InlineData("GET", "/MediaReports/Metrics", true, HttpStatusCode.Forbidden, "forbidden")]
    [InlineData("GET", "/MediaReports/Nothing/Here", true, HttpStatusCode.NotFound, "not-found")]
    [InlineData("PUT", "/MediaReports/Info", true, HttpStatusCode.MethodNotAllowed, "method-not-allowed")]
    public async Task ARefusalWithoutABody_GetsTheErrorBody(string method, string path, bool user, HttpStatusCode expected, string code)
    {
        var (status, body) = await Send(new HttpMethod(method), path, user);

        Assert.Equal(expected, status);
        Assert.Equal(code, Field(body, "Code"));
        Assert.False(string.IsNullOrEmpty(Field(body, "Message")));
    }

    [Fact]
    public async Task ARefusalOutsideThePlugin_IsLeftAlone()
    {
        using var response = await _client!.GetAsync(new Uri("/Items/Nothing", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Info_TellsASignedInUserTheVersionAndApiVersion()
    {
        var (status, body) = await Send(HttpMethod.Get, "/MediaReports/Info", user: true);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("1", Field(body, "ApiVersion"));
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version!.ToString(), Field(body, "Version"));
        Assert.Equal("True", Field(body, "ReportingEnabled"));
    }

    [Theory]
    [InlineData("/MediaReports/Info", false, "unauthorized")]
    [InlineData("/MediaReports/Nothing/Here", true, "not-found")]
    public async Task MiddlewareErrors_FollowTheCamelCaseProfile_WhenAskedFor(string path, bool user, string code)
    {
        var (_, camel) = await Send(HttpMethod.Get, path, user, accept: "application/json; profile=\"CamelCase\"");
        var (_, pascal) = await Send(HttpMethod.Get, path, user);

        Assert.Equal(["code", "message"], camel.EnumerateObject().Select(p => p.Name));
        Assert.Equal(code, camel.GetProperty("code").GetString());
        Assert.Equal(["Code", "Message"], pascal.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void EveryController_CarriesTheErrorFilter()
    {
        var controllers = typeof(ReportController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t)).ToList();

        Assert.NotEmpty(controllers);
        Assert.All(controllers, c => Assert.True(c.IsDefined(typeof(ApiErrorsAttribute), true), c.Name + " lacks [ApiErrors]"));
    }

    // Reads a field without regard to case, as a client of Jellyfin does.
    private static string? Field(JsonElement body, string name)
    {
        var value = body.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => value.GetRawText(),
        };
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, bool user, string? json = null, string? accept = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        if (user)
        {
            request.Headers.Add("X-Test-User", "alice");
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _client!.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrEmpty(text), $"{method} {path} answered {(int)response.StatusCode} with no body");
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private sealed class TestSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var name))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name.ToString())], "Test");
            if (Request.Headers.ContainsKey("X-Test-Admin"))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, "Administrator"));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
