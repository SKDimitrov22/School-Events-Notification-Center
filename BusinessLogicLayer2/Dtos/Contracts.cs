namespace BusinessLogicLayer2.Dtos;

public sealed record UserInfo(string Id, string Email, string Role, string DisplayName);

public sealed record LoginRequest(string Email, string Password = "");

public sealed record SignupRequest(string Email, string Password, string Role, string DisplayName);

public sealed record LoginResponse(string Token, UserInfo User);

public sealed record EventSummary(
    string Id,
    string OrganizerId,
    string Title,
    string Description,
    string StartsAt,
    string EndsAt,
    int Capacity,
    string? Location,
    string Status,
    int ConfirmedCount,
    int WaitlistCount,
    bool IsFull,
    string? CurrentUserRegistrationId,
    string? CurrentUserRegistrationStatus,
    int? CurrentUserWaitlistPosition);

public sealed record RegistrationSummary(
    string Id,
    string EventId,
    string EventTitle,
    string UserId,
    string UserDisplayName,
    string UserEmail,
    string Status,
    string RegisteredAt,
    int? WaitlistPosition);

public sealed class EventUpsertRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string StartsAt { get; set; } = string.Empty;
    public string EndsAt { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public string? Location { get; set; }
}
