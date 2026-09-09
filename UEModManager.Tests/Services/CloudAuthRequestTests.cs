using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class CloudAuthRequestTests
{
    [Fact]
    public async Task Login_SendsCredentialsAndCurrentVersion_WithoutMachineMetadata()
    {
        using var handler = new AuthHandler();
        using var client = new HttpClient(handler);
        var service = NewService(client);

        var result = await service.LoginAsync("user@example.test", "test-password");

        Assert.True(result.IsSuccess);
        Assert.True(service.IsConnected);
        Assert.Equal(7, service.CurrentUser?.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/auth/login", request.Path);
        Assert.Equal(new[] { "email", "password" }, request.Fields.Keys.OrderBy(key => key));
        Assert.Equal("user@example.test", request.Fields["email"]);
        Assert.Equal("test-password", request.Fields["password"]);
        Assert.Equal($"UEModManager/{typeof(CloudAuthService).Assembly.GetName().Version!.ToString(3)}", request.UserAgent);
    }

    [Fact]
    public async Task Register_SendsAccountFieldsOnly_AndStillLogsInAfterSuccess()
    {
        using var handler = new AuthHandler();
        using var client = new HttpClient(handler);
        var service = NewService(client);

        var result = await service.RegisterAsync("user@example.test", "test-password", "Display name");

        Assert.True(result.IsSuccess);
        Assert.True(service.IsConnected);
        Assert.Equal(2, handler.Requests.Count);
        var register = handler.Requests[0];
        Assert.Equal("/api/auth/register", register.Path);
        Assert.Equal(new[] { "email", "password", "username" }, register.Fields.Keys.OrderBy(key => key));
        Assert.Equal("Display name", register.Fields["username"]);
        Assert.Equal("/api/auth/login", handler.Requests[1].Path);
        Assert.Equal("user@example.test", handler.Requests[1].Fields["email"]);
    }

    private static CloudAuthService NewService(HttpClient client) =>
        new(client, NullLogger<CloudAuthService>.Instance, new CloudConfig { ApiBaseUrl = "https://auth.example.test" });

    private sealed record CapturedRequest(string Path, string UserAgent, Dictionary<string, string> Fields);

    private sealed class AuthHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(new CapturedRequest(request.RequestUri!.AbsolutePath, request.Headers.UserAgent.ToString(),
                body.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()!)));
            var json = request.RequestUri.AbsolutePath == "/api/auth/register"
                ? """{"success":true,"user_id":7}"""
                : """{"success":true,"access_token":"test-token","expires_in":3600,"user":{"id":7,"email":"user@example.test"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
