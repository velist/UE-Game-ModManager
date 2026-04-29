using UEModManager.Logging;

namespace UEModManager.Core.Tests.Logging;

public class LogRedactorTests
{
    [Fact]
    public void Redact_NullOrEmpty_ReturnsAsIs()
    {
        Assert.Equal("", LogRedactor.Redact(null));
        Assert.Equal("", LogRedactor.Redact(""));
    }

    [Fact]
    public void Redact_PlainText_NoChange()
    {
        var text = "Application started, no secrets here.";
        Assert.Equal(text, LogRedactor.Redact(text));
    }

    [Fact]
    public void Redact_Email_KeepsFirstCharAndDomain()
    {
        var redacted = LogRedactor.Redact("user logged in: john.doe@example.com");

        Assert.Contains("j***@example.com", redacted);
        Assert.DoesNotContain("john.doe", redacted);
    }

    [Fact]
    public void Redact_MultipleEmails_AllRedacted()
    {
        var redacted = LogRedactor.Redact("from: a.b@x.com to: contact@y.org");

        Assert.Contains("a***@x.com", redacted);
        Assert.Contains("c***@y.org", redacted);
    }

    [Fact]
    public void Redact_Jwt_FullyReplaced()
    {
        var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var redacted = LogRedactor.Redact($"token: {jwt} ok");

        Assert.Contains("[REDACTED-JWT]", redacted);
        Assert.DoesNotContain(jwt, redacted);
    }

    [Fact]
    public void Redact_BearerHeader_Replaced()
    {
        var redacted = LogRedactor.Redact("HTTP request: Authorization: abcd1234efgh5678ijkl");

        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("abcd1234efgh5678ijkl", redacted);
    }

    [Theory]
    [InlineData("api_key=abc123secret456", "api_key=")]
    [InlineData("apiKey: abc123secret456", "apiKey: ")]
    [InlineData("password = mySuperSecret", "password = ")]
    [InlineData("ACCESS_KEY=AKIAxxxxxxxxxxxxxxxxxx", "ACCESS_KEY=")]
    public void Redact_KeyValSecrets_PreservesKeyOnly(string input, string expectedPrefix)
    {
        var redacted = LogRedactor.Redact(input);

        Assert.StartsWith(expectedPrefix, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_JsonStyleSecret_Replaced()
    {
        var redacted = LogRedactor.Redact(@"{""token"":""mytokenvalue123""}");

        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("mytokenvalue123", redacted);
    }

    [Fact]
    public void Redact_PreservesSurroundingContext()
    {
        var redacted = LogRedactor.Redact("[Auth] login by alice@corp.com succeeded after retry");

        Assert.StartsWith("[Auth] login by ", redacted);
        Assert.EndsWith(" succeeded after retry", redacted);
        Assert.Contains("@corp.com", redacted);
    }

    [Fact]
    public void Redact_ShortKeyValBelowThreshold_NotMatched()
    {
        // < 6 字符的 secret 值不被规则匹配（避免误报）
        var input = "token=ab";
        var redacted = LogRedactor.Redact(input);

        Assert.Equal(input, redacted);
    }
}
