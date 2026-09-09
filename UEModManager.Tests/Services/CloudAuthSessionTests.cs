using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Data;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class CloudAuthSessionTests
{
    private const string LoginJson = """{"success":true,"access_token":"test-token","expires_in":3600,"user":{"id":7,"email":"user@example.test"}}""";

    [Fact]
    public async Task Register_RequiringConfirmation_DoesNotAttemptLoginOrClaimConnectedSession()
    {
        using var handler = new Handler(_ => Response("""{"success":true,"message":"Check your email","user_id":7,"verification_required":true,"verification_email_sent":true}"""));
        using var client = NewClient(handler);
        var service = NewService(client);

        var result = await service.RegisterAsync("USER@EXAMPLE.TEST", "test-password");

        Assert.True(result.IsSuccess);
        Assert.True(result.RequiresEmailVerification);
        Assert.True(UnifiedAuthResult.FromCloudResult(result, AuthSource.Cloud).RequiresEmailVerification);
        Assert.False(service.IsConnected);
        Assert.Null(service.CurrentUser);
        Assert.Equal("/api/auth/register", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task UnifiedRegister_PendingCloudConfirmation_DoesNotFallBackToLocalRegistration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        var local = new LocalAuthService(database, NullLogger<LocalAuthService>.Instance);
        using var handler = new Handler(_ => Response("""{"success":true,"message":"Check your email","user_id":7,"verification_required":true,"verification_email_sent":true}"""));
        using var client = NewClient(handler);
        var cloud = NewService(client);
        var unified = new UnifiedAuthService(local, cloud, NullLogger<UnifiedAuthService>.Instance, client);

        var result = await unified.RegisterAsync("user@example.test", "Test-password-1");

        Assert.True(result.IsSuccess);
        Assert.True(result.RequiresEmailVerification);
        Assert.Equal(AuthSource.Cloud, result.Source);
        Assert.False(unified.IsOnline);
        Assert.False(local.IsLoggedIn);
        Assert.Empty(await database.Users.ToListAsync());
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/auth/login");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_UpstreamOrNetworkFailure_AlwaysClearsDesktopSession(bool networkFailure)
    {
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("login")) return Response(LoginJson);
            if (networkFailure) throw new HttpRequestException("fixture network failure");
            return Response("""{"success":false,"message":"unavailable"}""", HttpStatusCode.BadGateway);
        });
        using var client = NewClient(handler);
        var service = NewService(client);
        Assert.True((await service.LoginAsync("user@example.test", "test-password")).IsSuccess);
        var events = new List<CloudAuthEventType>();
        service.AuthStateChanged += (_, e) => events.Add(e.EventType);

        Assert.False(await service.LogoutAsync());

        Assert.False(service.IsConnected);
        Assert.Null(service.CurrentUser);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal(new[] { CloudAuthEventType.SignedOut }, events);
        Assert.Equal("Bearer test-token", handler.Requests[^1].Authorization);
    }

    [Fact]
    public async Task Validate_InvalidServerSession_ClearsLocalStateWithoutAnotherLogoutRequest()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("login")
            ? Response(LoginJson) : Response("""{"valid":false}""", HttpStatusCode.Unauthorized));
        using var client = NewClient(handler);
        var service = NewService(client);
        Assert.True((await service.LoginAsync("user@example.test", "test-password")).IsSuccess);

        Assert.False(await service.ValidateTokenAsync());

        Assert.False(service.IsConnected);
        Assert.Null(service.CurrentUser);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/auth/validate", handler.Requests[1].Path);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Equal("Bearer test-token", handler.Requests[1].Authorization);
    }

    [Theory]
    [InlineData("""{"success":true,"user":{"id":7,"email":"user@example.test"}}""")]
    [InlineData("""{"success":true,"access_token":"token","expires_in":3600}""")]
    [InlineData("""{"success":true,"access_token":"token","expires_in":0,"user":{"id":7,"email":"user@example.test"}}""")]
    [InlineData("""{"success":true,"access_token":"token","expires_in":3600,"user":{"id":7,"email":"other@example.test"}}""")]
    public async Task Login_IncompleteOrDifferentIdentityResponse_IsNotAccepted(string body)
    {
        using var handler = new Handler(_ => Response(body));
        using var client = NewClient(handler);
        var service = NewService(client);

        Assert.False((await service.LoginAsync("user@example.test", "test-password")).IsSuccess);
        Assert.False(service.IsConnected);
        Assert.Null(service.CurrentUser);
    }

    private static HttpClient NewClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://auth.example.test") };
    private static CloudAuthService NewService(HttpClient client) =>
        new(client, NullLogger<CloudAuthService>.Instance, new CloudConfig { ApiBaseUrl = "https://auth.example.test" });
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Method, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Method.Method, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}
