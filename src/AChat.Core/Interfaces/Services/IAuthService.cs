using AChat.Core.DTOs.Auth;

namespace AChat.Core.Interfaces.Services;

public interface IAuthService
{
    Task<LoginResponse?> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<MeResponse?> GetCurrentUserAsync(Guid userId, CancellationToken ct = default);
    Task<bool> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default);
}
