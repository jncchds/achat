namespace AChat.Core.DTOs.Auth;

public record LoginRequest(string Username, string Password);

public record LoginResponse(string Token, string Username, string Role, Guid UserId);

public record UpdateProfileRequest(string? Username, string? Email, string? CurrentPassword, string? NewPassword);

public record MeResponse(Guid Id, string Username, string Email, string Role, long? TelegramId);
