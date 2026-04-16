using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ArrowThing.Server.Auth;

public sealed class AdminKeyAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AdminKey";
    public const string HeaderName = "X-Admin-Key";

    public AdminKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    )
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = Context.RequestServices.GetRequiredService<IConfiguration>()["Admin:ApiKey"];
        if (string.IsNullOrEmpty(expected))
            return Task.FromResult(AuthenticateResult.NoResult());

        var provided = Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!PasswordHasher.FixedTimeEquals(provided, expected))
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin key."));

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "admin") },
            SchemeName
        );
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
