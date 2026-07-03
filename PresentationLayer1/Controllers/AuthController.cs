using BusinessLogicLayer2.Dtos;
using BusinessLogicLayer2.Services;
using Microsoft.AspNetCore.Mvc;

namespace PresentationLayer1.Controllers;

[ApiController]
[Route("api")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var response = await authService.LoginAsync(request, cancellationToken);
        return response is null ? Unauthorized(new { error = "Invalid email or password." }) : response;
    }

    [HttpPost("signup")]
    public async Task<ActionResult> Signup(SignupRequest request, CancellationToken cancellationToken)
    {
        var success = await authService.SignUpAsync(
            new BusinessLogicLayer2.Dtos.SignupRequest(request.Email, request.Password, request.Role, request.DisplayName),
            cancellationToken);

        return !success
            ? Conflict(new { error = "Email already registered." })
            : Ok(new { success = true });
    }
}
