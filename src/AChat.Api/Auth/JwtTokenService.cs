using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AChat.Core.Models;
using Microsoft.IdentityModel.Tokens;

namespace AChat.Api.Auth;

/// <summary>
/// Issues and describes JWT bearer tokens (§7.1). The signing key comes from config
/// (<c>AChat_Jwt__SigningKey</c> / <c>ACHAT_Jwt__SigningKey</c>); when none is set a
/// process-ephemeral key is generated and a warning logged — tokens then die with the
/// process, which is acceptable for a throwaway local run but not for production.
/// </summary>
public sealed class JwtTokenService
{
    public const string Issuer = "achat";
    public const string Audience = "achat";

    public string SigningKey { get; }
    public int ExpiryHours { get; }

    public JwtTokenService(IConfiguration configuration)
    {
        var configured = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            Console.Error.WriteLine(
                "[AChat] WARNING: Jwt:SigningKey is not set — using an ephemeral signing key. " +
                "All tokens will be invalidated on restart. Set ACHAT_Jwt__SigningKey for production.");
        }
        else
        {
            SigningKey = configured;
        }
        ExpiryHours = configuration.GetValue("Jwt:ExpiryHours", 168);
    }

    public SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(SigningKey));

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = Key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = "name",
        RoleClaimType = "role",
    };

    public string CreateToken(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("name", user.Username),
            new("displayName", user.DisplayName),
        };
        if (user.IsAdmin) claims.Add(new("role", "admin"));

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddHours(ExpiryHours).UtcDateTime,
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
