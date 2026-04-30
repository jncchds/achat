using System.ComponentModel.DataAnnotations;

namespace AChat.Api.Models.Auth;

public record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record SetTelegramIdRequest(
    [Required] long TelegramId);

public record AuthResponse(string Token, Guid UserId, string? Email, bool IsAdmin);
