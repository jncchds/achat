using AChat.Core.DTOs.Users;

namespace AChat.Core.Interfaces.Services;

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> GetAllUsersAsync(CancellationToken ct = default);
    Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);
    Task<bool> DeleteUserAsync(Guid id, CancellationToken ct = default);
}
