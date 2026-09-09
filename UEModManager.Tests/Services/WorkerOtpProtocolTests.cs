using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class WorkerOtpProtocolTests
{
    private static readonly string Challenge = new('a', 64);
    private static string RequestSuccess => JsonSerializer.Serialize(new { success = true, challenge_id = Challenge, expires_in = 600, retry_after = 60 });

    [Fact]
    public async Task Login_RequestsServerChallenge_ThenRequiresServerVerificationAndConsumesIt()
    {
        using var handler = new Handler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("request")) return Response(RequestSuccess);
            return body.GetProperty("code").GetString() == "123456"
                ? Response("""{"success":true,"verified_email":"user@example.test","purpose":"login"}""")
                : Response("""{"success":false,"message":"invalid code"}""", HttpStatusCode.Unauthorized);
        });
        using var client = NewClient(handler);
        using var worker = NewWorker(client);
        var service = NewService(worker);

        Assert.True((await service.SendOtpAsync(" USER@EXAMPLE.TEST ")).Success);
        var sent = handler.Requests[0];
        Assert.Equal("/api/auth/otp/request", sent.Path);
        Assert.Equal(new[] { "email", "purpose" }, sent.Fields.Keys.OrderBy(key => key));
        Assert.Equal("user@example.test", sent.Fields["email"]);
        Assert.Equal("login", sent.Fields["purpose"]);

        Assert.False((await service.VerifyOtpAsync("user@example.test", "654321")).Success);
        Assert.True((await service.VerifyOtpAsync(" USER@EXAMPLE.TEST ", "123456")).Success);
        Assert.False((await service.VerifyOtpAsync("user@example.test", "123456")).Success);
        Assert.Equal(3, handler.Requests.Count);
        var verified = handler.Requests[2];
        Assert.Equal("/api/auth/otp/verify", verified.Path);
        Assert.Equal(new[] { "challenge_id", "code", "email", "purpose" }, verified.Fields.Keys.OrderBy(key => key));
        Assert.Equal(Challenge, verified.Fields["challenge_id"]);
        Assert.Equal("123456", verified.Fields["code"]);
    }

    [Fact]
    public async Task Verify_WithoutAChallenge_CannotCreateAnAuthenticatedResult()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No network request expected"));
        using var client = NewClient(handler);
        using var worker = NewWorker(client);

        Assert.False((await NewService(worker).VerifyOtpAsync("user@example.test", "123456")).Success);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"success":true,"verified_email":"other@example.test","purpose":"login"}""")]
    [InlineData("""{"success":true,"verified_email":"user@example.test","purpose":"reset"}""")]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":false,"verified_email":"user@example.test","purpose":"login"}""")]
    public async Task Verify_RequiresConfirmedEmailAndPurpose_NotOnlyAnHttpSuccess(string reply)
    {
        using var handler = new Handler((_, _) => Response(reply));
        using var client = NewClient(handler);
        using var worker = NewWorker(client);

        Assert.False((await worker.VerifyLoginCodeAsync("user@example.test", Challenge, "123456")).Success);
    }

    [Fact]
    public async Task Send_RateLimited_PropagatesRetryAfterWithoutAClientMailFallback()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response("""{"success":false,"message":"too many requests","retry_after":12}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(37));
            return response;
        });
        using var client = NewClient(handler);
        using var worker = NewWorker(client);

        var result = await NewService(worker).SendOtpAsync("user@example.test");

        Assert.False(result.Success);
        Assert.Equal(37, result.RetryAfterSeconds);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":true,"challenge_id":"123456","expires_in":600}""")]
    [InlineData("""{"success":true,"challenge_id":123,"expires_in":"600"}""")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task Send_MalformedSuccess_DoesNotCacheALocallyVerifiableCode(string reply)
    {
        using var handler = new Handler((_, _) => Response(reply));
        using var client = NewClient(handler);
        using var worker = NewWorker(client);
        var service = NewService(worker);

        Assert.False((await service.SendOtpAsync("user@example.test")).Success);
        Assert.False((await service.VerifyOtpAsync("user@example.test", "123456")).Success);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Verify_NetworkFailure_DoesNotTreatTheInputCodeAsProof()
    {
        using var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("request")
            ? Response(RequestSuccess) : throw new HttpRequestException("fixture network failure"));
        using var client = NewClient(handler);
        using var worker = NewWorker(client);
        var service = NewService(worker);

        Assert.True((await service.SendOtpAsync("user@example.test")).Success);
        Assert.False((await service.VerifyOtpAsync("user@example.test", "123456")).Success);
    }

    private static HttpClient NewClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://auth.example.test") };
    private static WorkerEmailService NewWorker(HttpClient client) => new(client, NullLogger<WorkerEmailService>.Instance);
    private static CustomOtpService NewService(WorkerEmailService worker) => new(NullLogger<CustomOtpService>.Instance, worker);
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, JsonElement, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, Dictionary<string, string> Fields)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.RequestUri!.AbsolutePath,
                body.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()!)));
            return respond(request, body.RootElement);
        }
    }
}
