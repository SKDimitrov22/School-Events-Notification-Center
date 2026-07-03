namespace DataAccessLayer3.Repositories;

public interface ISchoolEventsRepository
{
    Task<UserRecord?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<bool> CreateUserAsync(string email, string passwordHash, string role, string displayName, CancellationToken cancellationToken = default);
    Task<UserRecord?> GetUserByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EventRecord>> GetEventsAsync(UserRecord? user, CancellationToken cancellationToken = default);
    Task<EventRecord?> GetEventAsync(string id, UserRecord? user, CancellationToken cancellationToken = default);
    Task<EventRecord> CreateEventAsync(string organizerId, EventUpsertData input, CancellationToken cancellationToken = default);
    Task<EventRecord?> UpdateEventAsync(string id, string organizerId, EventUpsertData input, CancellationToken cancellationToken = default);
    Task<EventRecord?> SetEventStatusAsync(string id, string organizerId, string status, CancellationToken cancellationToken = default);
    Task<RegistrationRecord?> RegisterAsync(string eventId, string userId, CancellationToken cancellationToken = default);
    Task<bool> CancelRegistrationAsync(string registrationId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistrationRecord>> GetRegistrationsForUserAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistrationRecord>> GetRegistrationsForEventAsync(string eventId, string organizerId, string status, CancellationToken cancellationToken = default);
}
