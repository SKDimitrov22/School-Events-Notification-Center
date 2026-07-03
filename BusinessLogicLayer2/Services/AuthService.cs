using System.Text;
using System.Text.Json;
using BusinessLogicLayer2.Dtos;
using DataAccessLayer3.Repositories;
using System.Security.Cryptography;

namespace BusinessLogicLayer2.Services;

public sealed class AuthService(ISchoolEventsRepository repository) : IAuthService
{
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await repository.GetUserByEmailAsync(request.Email, cancellationToken);
        if (user is null || !PasswordMatches(request.Password, user.PasswordHash))
        {
            return null;
        }

        var info = new UserInfo(user.Id, user.Email, user.Role, user.DisplayName);
        return new LoginResponse(GenerateJwt(info), info);
    }

    public async Task<UserInfo?> GetUserAsync(string id, CancellationToken cancellationToken = default)
    {
        var user = await repository.GetUserByIdAsync(id, cancellationToken);
        return user is null ? null : new UserInfo(user.Id, user.Email, user.Role, user.DisplayName);
    }

    public async Task<bool> SignUpAsync(SignupRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetUserByEmailAsync(request.Email, cancellationToken);
        if (existing is not null)
        {
            return false;
        }

        var role = string.Equals(request.Role, "ORGANIZER", StringComparison.OrdinalIgnoreCase)
            ? "ORGANIZER"
            : "STUDENT";

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        return await repository.CreateUserAsync(request.Email, passwordHash, role, request.DisplayName, cancellationToken);
    }

    private static bool PasswordMatches(string password, string passwordHash)
    {
        if (passwordHash.StartsWith("$2", StringComparison.Ordinal))
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }

        return password == passwordHash;
    }

    private static string GenerateJwt(UserInfo user)
    {
        var issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "school-events";
        var audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "school-events";
        var expiresMinutes = int.TryParse(Environment.GetEnvironmentVariable("JWT_EXPIRES_MINUTES"), out var minutes) ? minutes : 120;
        var now = DateTimeOffset.UtcNow;

        var header = B64U(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }));
        var payload = B64U(JsonSerializer.Serialize(new
        {
            sub = user.Id,
            email = user.Email,
            role = user.Role,
            name = user.DisplayName,
            iss = issuer,
            aud = audience,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(expiresMinutes).ToUnixTimeSeconds(),
        }));

        var unsignedToken = $"{header}.{payload}";
        return $"{unsignedToken}.{Sign(unsignedToken)}";
    }

    private static string B64U(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');

    private static string Sign(string value)
    {
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET")
            ?? "dev-only-insecure-secret-change-before-any-real-deployment";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    }
}
