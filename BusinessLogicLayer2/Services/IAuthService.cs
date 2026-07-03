using BusinessLogicLayer2.Dtos;

namespace BusinessLogicLayer2.Services;

public interface IAuthService
{
    Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<bool> SignUpAsync(SignupRequest request, CancellationToken cancellationToken = default);
    Task<UserInfo?> GetUserAsync(string id, CancellationToken cancellationToken = default);
}
