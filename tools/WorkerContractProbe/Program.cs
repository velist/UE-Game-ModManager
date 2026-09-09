using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint)
    || endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)
    throw new ArgumentException("Pass the loopback URL from the test-only Worker runtime.");

var checks = 0;
var suffix = Guid.NewGuid().ToString("N")[..10];
var email = $"contract-{suffix}@example.test";
var config = new CloudConfig { ApiBaseUrl = endpoint.AbsoluteUri, RequestTimeoutSeconds = 10 };

HttpClient Client()
{
    var client = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(10) };
    client.DefaultRequestHeaders.Add("cf-connecting-ip", "192.0.2.222");
    return client;
}

void Check(bool passed, string name)
{
    if (!passed) throw new InvalidOperationException("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}

using var loginClient = Client();
var login = new CloudAuthService(loginClient, NullLogger<CloudAuthService>.Instance, config);
var signedIn = await login.LoginAsync($"  {email.ToUpperInvariant()}  ", "valid-password");
Check(signedIn.IsSuccess && login.IsConnected, "desktop password login accepts Worker response");
Check(signedIn.User?.Email == email && signedIn.User.Id >= 0, "normalized identity and int32 user mapping");
Check(await login.ValidateTokenAsync(), "desktop session validation consumes actual Worker contract");
var accessToken = loginClient.DefaultRequestHeaders.Authorization;
Check(await login.LogoutAsync() && !login.IsConnected && login.CurrentUser == null,
    "desktop logout clears local authentication");
using (var revokedClient = Client())
{
    revokedClient.DefaultRequestHeaders.Authorization = accessToken;
    using var revoked = await revokedClient.GetAsync("/api/auth/validate");
    Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "Worker logout revokes upstream session");
}

using var registerClient = Client();
var register = new CloudAuthService(registerClient, NullLogger<CloudAuthService>.Instance, config);
var registered = await register.RegisterAsync($"new-{suffix}@example.test", "valid-password", "Contract User");
Check(registered.IsSuccess && register.IsConnected, "desktop registration follows Worker login contract");
Check(await register.ValidateTokenAsync(), "new registration session can be validated");

using var confirmClient = Client();
var confirm = new CloudAuthService(confirmClient, NullLogger<CloudAuthService>.Instance, config);
var confirmation = await confirm.RegisterAsync($"confirm-{suffix}@example.test", "valid-password", "Pending User");
Check(confirmation.IsSuccess && confirmation.RequiresEmailVerification && !confirm.IsConnected,
    "email confirmation is successful pending verification, without a false login");

using var wrongClient = Client();
var wrong = new CloudAuthService(wrongClient, NullLogger<CloudAuthService>.Instance, config);
Check(!(await wrong.LoginAsync($"wrong-{suffix}@example.test", "wrong-password")).IsSuccess && !wrong.IsConnected,
    "rejected password does not establish desktop authentication");

using var otpClient = Client();
using var worker = new WorkerEmailService(otpClient, NullLogger<WorkerEmailService>.Instance);
var otp = new CustomOtpService(NullLogger<CustomOtpService>.Instance, worker);
var otpEmail = $"otp-{suffix}@example.test";
var request = await otp.SendOtpAsync($" {otpEmail.ToUpperInvariant()} ");
Check(request.Success && request.RetryAfterSeconds > 0, "desktop requests server-owned login challenge");
using var fixtureClient = Client();
using var mailbox = JsonDocument.Parse(await fixtureClient.GetStringAsync(
    "/__fixture/mail?email=" + Uri.EscapeDataString(otpEmail)));
Check(mailbox.RootElement.GetArrayLength() == 1, "one fixed login email reached only the test mailbox");
var message = mailbox.RootElement[0];
var text = message.GetProperty("textContent").GetString()!;
var match = Regex.Match(text, @"(?<!\d)\d{6}(?!\d)");
Check(match.Success, "server-generated verification code is present in test email");
var code = match.Value;
var wrongCode = code == "000000" ? "999999" : "000000";
Check(!(await otp.VerifyOtpAsync(otpEmail, wrongCode)).Success, "server rejects an incorrect login code");
Check((await otp.VerifyOtpAsync(otpEmail, code)).Success, "desktop verifies the real server-owned code");
Check(!(await otp.VerifyOtpAsync(otpEmail, code)).Success, "desktop cannot reuse a completed challenge");

using var relay = await fixtureClient.PostAsJsonAsync("/email/send", new
{
    to = $"relay-{suffix}@example.test", subject = "UEModManager arbitrary content", html = "<p>caller content</p>"
});
Check(relay.StatusCode == HttpStatusCode.Gone, "legacy arbitrary-content mail relay is unavailable");
await relay.Content.ReadAsStringAsync();
relay.Dispose();
using var relayMailbox = JsonDocument.Parse(await fixtureClient.GetStringAsync(
    "/__fixture/mail?email=" + Uri.EscapeDataString($"relay-{suffix}@example.test")));
Check(relayMailbox.RootElement.GetArrayLength() == 0, "rejected relay produces no mail");

Console.WriteLine(JsonSerializer.Serialize(new { Passed = checks, Failed = 0, Runtime = "local workerd", RealEmailsSent = 0 }));
