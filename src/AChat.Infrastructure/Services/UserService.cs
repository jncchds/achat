using AChat.Core.DTOs.Users;
using AChat.Core.Entities;
using AChat.Core.Interfaces.Services;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AChat.Infrastructure.Services;

public partial class UserService(
    AppDbContext db,
    ILogger<UserService> logger) : IUserService
{
    public async Task<IReadOnlyList<UserDto>> GetAllUsersAsync(CancellationToken ct = default)
    {
        return await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => ToDto(u))
            .ToListAsync(ct);
    }

    public async Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? null : ToDto(user);
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        var user = new User
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = request.Role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        LogUserCreated(logger, user.Id, user.Username);
        return ToDto(user);
    }

    public async Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return null;

        if (request.Username is not null) user.Username = request.Username;
        if (request.Email is not null) user.Email = request.Email;
        if (request.Password is not null) user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        if (request.Role is not null) user.Role = request.Role.Value;
        if (request.IsActive is not null) user.IsActive = request.IsActive.Value;
        if (request.ClearTelegramId) user.TelegramId = null;
        else if (request.TelegramId is not null) user.TelegramId = request.TelegramId;
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        LogUserUpdated(logger, id);
        return ToDto(user);
    }

    public async Task<bool> DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return false;
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        LogUserDeleted(logger, id);
        return true;
    }

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Username, u.Email, u.Role.ToString(), u.TelegramId, u.IsActive, u.CreatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} ({Username}) created")]
    private static partial void LogUserCreated(ILogger logger, Guid userId, string username);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} updated")]
    private static partial void LogUserUpdated(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} deleted")]
    private static partial void LogUserDeleted(ILogger logger, Guid userId);
}
