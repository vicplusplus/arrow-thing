using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ArrowThing.Server.Models;
using Microsoft.IdentityModel.Tokens;

namespace ArrowThing.Server.Auth;

public class JwtHelper
{
    private readonly byte[] _key;

    private const string Issuer = "arrow-thing-api";
    private const string Audience = "arrow-thing-client";

    /// <summary>
    /// Access-token lifetime. Short on purpose: paired with the refresh-token
    /// flow (Phase 1C), this caps the XSS exposure window for a stolen JWT
    /// at the lifetime length.
    /// </summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    public JwtHelper(IConfiguration configuration)
    {
        var secret =
            configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

        if (secret.Length < 32)
            throw new InvalidOperationException("Jwt:Secret must be at least 32 characters long.");

        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string GenerateToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("display_name", user.DisplayName),
            new Claim("security_stamp", user.SecurityStamp),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(_key),
            SecurityAlgorithms.HmacSha256
        );

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(AccessTokenLifetime),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters GetValidationParameters() =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(_key),
        };
}
