using System.ComponentModel.DataAnnotations;

namespace AChat.Api.Models.Admin;

public record CreateUserRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password);

public record UserResponse(Guid Id, string? Email, bool IsAdmin, DateTime CreatedAt);
