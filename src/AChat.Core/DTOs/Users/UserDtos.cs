using AChat.Core.Enums;

namespace AChat.Core.DTOs.Users;

public record UserDto(
    Guid Id,
    string Username,
    string Email,
    string Role,
    long? TelegramId,
    bool IsActive,
    DateTime CreatedAt);

public record CreateUserRequest(string Username, string Email, string Password, UserRole Role = UserRole.User);

public record UpdateUserRequest(
    string? Username,
    string? Email,
    string? Password,
    UserRole? Role,
    bool? IsActive,
    long? TelegramId,
    bool ClearTelegramId = false);
