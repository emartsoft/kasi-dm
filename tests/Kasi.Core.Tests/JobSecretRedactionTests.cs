using Kasi.Core;
using Kasi.Core.Logging;

namespace Kasi.Core.Tests;

[Trait("TC", "TC-ENG-016")]
[Trait("Requirement", "KASI-NFR-013")]
public sealed class JobSecretRedactionTests
{
    [Fact]
    public void Secret_models_never_render_their_values_in_ToString()
    {
        var cookie = new JobCookie { Name = "session", Value = "cookie-value", Domain = "example.com" };
        var credentials = new HttpCredentials { UserName = "alice", Password = "password-value" };
        var secret = new JobSecret
        {
            Cookies = [cookie],
            Headers = new Dictionary<string, string> { ["Authorization"] = "header-value" },
            Credentials = credentials,
        };

        cookie.ToString().Should().Be(Redact.Placeholder);
        credentials.ToString().Should().Be(Redact.Placeholder);
        secret.ToString().Should().Be(Redact.Placeholder);
    }
}
