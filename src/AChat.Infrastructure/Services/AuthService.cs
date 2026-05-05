using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AChat.Core.DTOs.Auth;
using AChat.Core.Entities;
using AChat.Core.Interfaces.Services;
using AChat.Core.Options;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AChat.Infrastructure.Services;

public partial class AuthService(
    AppDbContext db,
    IOptions<JwtOptions> jwtOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<LoginResponse?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username && u.IsActive, ct);

        if (user is null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            LogLoginFailed(logger, username);
            return null;
        }

        var token = GenerateToken(user);
        LogLoginSuccess(logger, username);
        return new LoginResponse(token, user.Username, user.Role.ToString(), user.Id);
    }

    public async Task<MeResponse?> GetCurrentUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? null : new MeResponse(user.Id, user.Username, user.Email, user.Role.ToString(), user.TelegramId);
    }

    public async Task<bool> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;

        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
                !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            {
                LogPasswordChangeFailed(logger, userId);
                return false;
            }
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        }

        if (!string.IsNullOrWhiteSpace(request.Username)) user.Username = request.Username;
        if (!string.IsNullOrWhiteSpace(request.Email)) user.Email = request.Email;
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        LogProfileUpdated(logger, userId);
        return true;
    }

    private string GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_jwt.ExpiryHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Login failed for user {Username}")]
    private static partial void LogLoginFailed(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {Username} logged in successfully")]
    private static partial void LogLoginSuccess(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Password change failed for user {UserId} - invalid current password")]
    private static partial void LogPasswordChangeFailed(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Profile updated for user {UserId}")]
    private static partial void LogProfileUpdated(ILogger logger, Guid userId);
}
