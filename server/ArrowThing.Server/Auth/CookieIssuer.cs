using Microsoft.Extensions.Options;

namespace ArrowThing.Server.Auth;

/// <summary>
/// Writes + clears the <c>arrow_access</c> and <c>arrow_refresh</c> HttpOnly
/// cookies that carry the Phase 1D auth session. Bound to
/// <c>Auth:Cookies</c> config so dev (no Domain, optionally not Secure) and
/// prod (<c>.arrow-thing.com</c>, Secure, SameSite=Lax) can differ.
///
/// SameSite=Lax (not Strict): cross-subdomain XHR from <c>arrow-thing.com</c>
/// to <c>api.arrow-thing.com</c> is same-site per the spec, but Firefox's
/// privacy posture rejects Strict cookies set via XHR response in some
/// configurations. Lax is accepted everywhere and still blocks the CSRF
/// attack class we care about (cross-site POST), with the Origin-check
/// middleware in Program.cs as defense in depth.
///
/// The tokens are also returned in the JSON response body (see <c>AuthResponse</c>)
/// so bearer-based clients — currently the Unity editor and any future native
/// app — keep working unchanged.
/// </summary>
public class CookieIssuer
{
    public const string AccessCookieName = "arrow_access";
    public const string RefreshCookieName = "arrow_refresh";

    private readonly CookieAuthOptions _options;

    public CookieIssuer(IOptions<CookieAuthOptions> options)
    {
        _options = options.Value;
    }

    public void IssueAccessCookie(HttpResponse response, string jwt)
    {
        response.Cookies.Append(
            AccessCookieName,
            jwt,
            BuildOptions(DateTimeOffset.UtcNow + JwtHelper.AccessTokenLifetime)
        );
    }

    public void IssueRefreshCookie(HttpResponse response, string refreshToken)
    {
        response.Cookies.Append(
            RefreshCookieName,
            refreshToken,
            BuildOptions(DateTimeOffset.UtcNow + RefreshTokenService.Lifetime)
        );
    }

    public void ClearCookies(HttpResponse response)
    {
        var expired = BuildOptions(DateTimeOffset.UnixEpoch);
        response.Cookies.Append(AccessCookieName, "", expired);
        response.Cookies.Append(RefreshCookieName, "", expired);
    }

    private CookieOptions BuildOptions(DateTimeOffset expires)
    {
        var opts = new CookieOptions
        {
            HttpOnly = true,
            Secure = _options.Secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expires,
        };
        if (!string.IsNullOrEmpty(_options.Domain))
            opts.Domain = _options.Domain;
        return opts;
    }
}

public class CookieAuthOptions
{
    /// <summary>Cookie <c>Domain</c> attribute. Empty → host-only (dev).</summary>
    public string Domain { get; set; } = "";

    /// <summary>Cookie <c>Secure</c> flag. False in local dev, true in prod.</summary>
    public bool Secure { get; set; } = true;
}
