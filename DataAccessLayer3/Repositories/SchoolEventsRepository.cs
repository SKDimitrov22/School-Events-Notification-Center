using System.Text.Json;
using DataAccessLayer3.Db;
using DataAccessLayer3.Models;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer3.Repositories;

public sealed class SchoolEventsRepository(AppDbContext db) : ISchoolEventsRepository
{
    public async Task<UserRecord?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Email == email, cancellationToken);
        return user is null ? null : ToRecord(user);
    }

    public async Task<UserRecord?> GetUserByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == id, cancellationToken);
        return user is null ? null : ToRecord(user);
    }

    public async Task<bool> CreateUserAsync(string email, string passwordHash, string role, string displayName, CancellationToken cancellationToken = default)
    {
        if (await GetUserByEmailAsync(email, cancellationToken) is not null)
        {
            return false;
        }

        db.Users.Add(new User
        {
            Id = NewId(),
            Email = email,
            PasswordHash = passwordHash,
            Role = role,
            DisplayName = displayName,
        });

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<EventRecord>> GetEventsAsync(UserRecord? user, CancellationToken cancellationToken = default)
    {
        var query = db.Events.AsNoTracking();
        query = user is not null && string.Equals(user.Role, "ORGANIZER", StringComparison.OrdinalIgnoreCase)
            ? query.Where(evt => evt.OrganizerId == user.Id)
            : query.Where(evt => evt.Status == "PUBLISHED");

        var events = await query.OrderBy(evt => evt.StartsAt).ToListAsync(cancellationToken);
        return await ToEventRecordsAsync(events, user, cancellationToken);
    }

    public async Task<EventRecord?> GetEventAsync(string id, UserRecord? user, CancellationToken cancellationToken = default)
    {
        var evt = await db.Events.AsNoTracking()
            .FirstOrDefaultAsync(evt => evt.Id == id, cancellationToken);
        if (evt is null)
        {
            return null;
        }

        var canSee = evt.Status == "PUBLISHED" ||
            user is not null && string.Equals(user.Role, "ORGANIZER", StringComparison.OrdinalIgnoreCase) && user.Id == evt.OrganizerId;
        return canSee ? await ToEventRecordAsync(evt, user, cancellationToken) : null;
    }

    public async Task<EventRecord> CreateEventAsync(string organizerId, EventUpsertData input, CancellationToken cancellationToken = default)
    {
        var evt = new SchoolEvent
        {
            Id = NewId(),
            OrganizerId = organizerId,
            Title = input.Title,
            Description = input.Description,
            StartsAt = input.StartsAt,
            EndsAt = input.EndsAt,
            Capacity = input.Capacity,
            Location = input.Location,
            Status = "DRAFT",
        };

        db.Events.Add(evt);
        await db.SaveChangesAsync(cancellationToken);

        var organizer = await GetUserByIdAsync(organizerId, cancellationToken);
        return (await GetEventAsync(evt.Id, organizer, cancellationToken))!;
    }

    public async Task<EventRecord?> UpdateEventAsync(string id, string organizerId, EventUpsertData input, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var evt = await db.Events.FirstOrDefaultAsync(
            evt => evt.Id == id && evt.OrganizerId == organizerId && evt.Status != "CANCELLED",
            cancellationToken);
        if (evt is null)
        {
            return null;
        }

        evt.Title = input.Title;
        evt.Description = input.Description;
        evt.StartsAt = input.StartsAt;
        evt.EndsAt = input.EndsAt;
        evt.Capacity = input.Capacity;
        evt.Location = input.Location;
        evt.UpdatedAt = DateTime.UtcNow;
        await PromoteWaitlistToCapacityAsync(evt.Id, evt.Capacity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var organizer = await GetUserByIdAsync(organizerId, cancellationToken);
        return await GetEventAsync(id, organizer, cancellationToken);
    }

    public async Task<EventRecord?> SetEventStatusAsync(string id, string organizerId, string status, CancellationToken cancellationToken = default)
    {
        var evt = await db.Events.FirstOrDefaultAsync(
            evt => evt.Id == id && evt.OrganizerId == organizerId,
            cancellationToken);
        if (evt is null)
        {
            return null;
        }

        var oldStatus = evt.Status;
        evt.Status = status;
        evt.UpdatedAt = DateTime.UtcNow;
        if (oldStatus != status && status == "CANCELLED")
        {
            Enqueue("EventCancelled", id, new { event_id = id });
        }
        else if (oldStatus != "PUBLISHED" && status == "PUBLISHED")
        {
            await PromoteWaitlistToCapacityAsync(evt.Id, evt.Capacity, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        var organizer = await GetUserByIdAsync(organizerId, cancellationToken);
        return await GetEventAsync(id, organizer, cancellationToken);
    }

    public async Task<RegistrationRecord?> RegisterAsync(string eventId, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var evt = await db.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (evt?.Status != "PUBLISHED")
        {
            return null;
        }

        var active = await db.Registrations.AnyAsync(
            registration => registration.EventId == eventId &&
                registration.UserId == userId &&
                (registration.Status == "CONFIRMED" || registration.Status == "WAITLISTED"),
            cancellationToken);
        if (active)
        {
            return null;
        }

        var confirmed = await db.Registrations.CountAsync(
            registration => registration.EventId == eventId && registration.Status == "CONFIRMED",
            cancellationToken);
        var registrationStatus = confirmed < evt.Capacity ? "CONFIRMED" : "WAITLISTED";
        var registration = new Registration
        {
            Id = NewId(),
            EventId = eventId,
            UserId = userId,
            Status = registrationStatus,
            RegisteredAt = DateTime.UtcNow.ToString("O"),
        };
        db.Registrations.Add(registration);
        Enqueue(
            registrationStatus == "CONFIRMED" ? "RegistrationConfirmed" : "RegistrationWaitlisted",
            registration.Id,
            new { event_id = eventId, user_id = userId, registration_id = registration.Id });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetRegistrationAsync(registration.Id, cancellationToken);
    }

    public async Task<bool> CancelRegistrationAsync(string registrationId, string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var registration = await db.Registrations.FirstOrDefaultAsync(
            registration => registration.Id == registrationId &&
                registration.UserId == userId &&
                (registration.Status == "CONFIRMED" || registration.Status == "WAITLISTED"),
            cancellationToken);
        if (registration is null)
        {
            return false;
        }

        var eventId = registration.EventId;
        var oldStatus = registration.Status;
        registration.Status = "CANCELLED";
        registration.CancelledAt = DateTime.UtcNow.ToString("O");
        Enqueue("RegistrationCancelled", registrationId, new { event_id = eventId, user_id = userId, registration_id = registrationId });

        if (oldStatus == "CONFIRMED")
        {
            await PromoteWaitlistToCapacityAsync(eventId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<RegistrationRecord>> GetRegistrationsForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var registrations = await db.Registrations.AsNoTracking()
            .Include(registration => registration.Event)
            .Include(registration => registration.User)
            .Where(registration => registration.UserId == userId &&
                (registration.Status == "CONFIRMED" || registration.Status == "WAITLISTED"))
            .OrderBy(registration => registration.Event!.StartsAt)
            .ToListAsync(cancellationToken);
        return await ToRegistrationRecordsAsync(registrations, cancellationToken);
    }

    public async Task<IReadOnlyList<RegistrationRecord>> GetRegistrationsForEventAsync(string eventId, string organizerId, string status, CancellationToken cancellationToken = default)
    {
        var ownsEvent = await db.Events.AnyAsync(
            evt => evt.Id == eventId && evt.OrganizerId == organizerId,
            cancellationToken);
        if (!ownsEvent)
        {
            return [];
        }

        var registrations = await db.Registrations.AsNoTracking()
            .Include(registration => registration.Event)
            .Include(registration => registration.User)
            .Where(registration => registration.EventId == eventId && registration.Status == status)
            .OrderBy(registration => registration.RegisteredAt)
            .ThenBy(registration => registration.Id)
            .ToListAsync(cancellationToken);
        return await ToRegistrationRecordsAsync(registrations, cancellationToken);
    }

    private async Task<RegistrationRecord?> GetRegistrationAsync(string id, CancellationToken cancellationToken)
    {
        var registration = await db.Registrations.AsNoTracking()
            .Include(registration => registration.Event)
            .Include(registration => registration.User)
            .FirstOrDefaultAsync(registration => registration.Id == id, cancellationToken);
        return registration is null ? null : await ToRegistrationRecordAsync(registration, cancellationToken);
    }

    private async Task<IReadOnlyList<EventRecord>> ToEventRecordsAsync(IEnumerable<SchoolEvent> events, UserRecord? user, CancellationToken cancellationToken)
    {
        var records = new List<EventRecord>();
        foreach (var evt in events)
        {
            records.Add(await ToEventRecordAsync(evt, user, cancellationToken));
        }

        return records;
    }

    private async Task<EventRecord> ToEventRecordAsync(SchoolEvent evt, UserRecord? user, CancellationToken cancellationToken)
    {
        var confirmed = await db.Registrations.CountAsync(
            registration => registration.EventId == evt.Id && registration.Status == "CONFIRMED",
            cancellationToken);
        var waitlist = await db.Registrations.CountAsync(
            registration => registration.EventId == evt.Id && registration.Status == "WAITLISTED",
            cancellationToken);

        string? registrationId = null;
        string? registrationStatus = null;
        int? waitlistPosition = null;
        if (user is not null && string.Equals(user.Role, "STUDENT", StringComparison.OrdinalIgnoreCase))
        {
            var registration = await db.Registrations.AsNoTracking()
                .FirstOrDefaultAsync(registration => registration.EventId == evt.Id &&
                    registration.UserId == user.Id &&
                    (registration.Status == "CONFIRMED" || registration.Status == "WAITLISTED"),
                    cancellationToken);
            if (registration is not null)
            {
                registrationId = registration.Id;
                registrationStatus = registration.Status;
                waitlistPosition = registration.Status == "WAITLISTED"
                    ? await WaitlistPositionAsync(evt.Id, registration.Id, cancellationToken)
                    : null;
            }
        }

        return new EventRecord(
            evt.Id,
            evt.OrganizerId,
            evt.Title,
            evt.Description,
            evt.StartsAt,
            evt.EndsAt,
            evt.Capacity,
            evt.Location,
            evt.Status,
            confirmed,
            waitlist,
            registrationId,
            registrationStatus,
            waitlistPosition);
    }

    private async Task<IReadOnlyList<RegistrationRecord>> ToRegistrationRecordsAsync(IEnumerable<Registration> registrations, CancellationToken cancellationToken)
    {
        var records = new List<RegistrationRecord>();
        foreach (var registration in registrations)
        {
            records.Add(await ToRegistrationRecordAsync(registration, cancellationToken));
        }

        return records;
    }

    private async Task<RegistrationRecord> ToRegistrationRecordAsync(Registration registration, CancellationToken cancellationToken)
    {
        int? waitlistPosition = registration.Status == "WAITLISTED"
            ? await WaitlistPositionAsync(registration.EventId, registration.Id, cancellationToken)
            : null;

        return new RegistrationRecord(
            registration.Id,
            registration.EventId,
            registration.Event!.Title,
            registration.UserId,
            registration.User!.DisplayName,
            registration.User.Email,
            registration.Status,
            registration.RegisteredAt,
            waitlistPosition);
    }

    private async Task<int> WaitlistPositionAsync(string eventId, string registrationId, CancellationToken cancellationToken)
    {
        var current = await db.Registrations.AsNoTracking()
            .FirstAsync(registration => registration.Id == registrationId, cancellationToken);
        var waitlist = await db.Registrations.AsNoTracking()
            .Where(registration => registration.EventId == eventId && registration.Status == "WAITLISTED")
            .Select(registration => new { registration.Id, registration.RegisteredAt })
            .ToListAsync(cancellationToken);

        return waitlist.Count(ahead =>
            string.Compare(ahead.RegisteredAt, current.RegisteredAt, StringComparison.Ordinal) < 0 ||
            ahead.RegisteredAt == current.RegisteredAt && string.Compare(ahead.Id, current.Id, StringComparison.Ordinal) < 0) + 1;
    }

    private async Task PromoteWaitlistToCapacityAsync(string eventId, CancellationToken cancellationToken)
    {
        var capacity = await db.Events
            .Where(evt => evt.Id == eventId)
            .Select(evt => evt.Capacity)
            .FirstAsync(cancellationToken);
        await PromoteWaitlistToCapacityAsync(eventId, capacity, cancellationToken);
    }

    private async Task PromoteWaitlistToCapacityAsync(string eventId, int capacity, CancellationToken cancellationToken)
    {
        var confirmed = await db.Registrations.CountAsync(
            registration => registration.EventId == eventId && registration.Status == "CONFIRMED",
            cancellationToken);
        var availableSeats = capacity - confirmed;
        if (availableSeats <= 0)
        {
            return;
        }

        var nextRegistrations = await db.Registrations
            .Where(registration => registration.EventId == eventId && registration.Status == "WAITLISTED")
            .OrderBy(registration => registration.RegisteredAt)
            .ThenBy(registration => registration.Id)
            .Take(availableSeats)
            .ToListAsync(cancellationToken);

        foreach (var registration in nextRegistrations)
        {
            registration.Status = "CONFIRMED";
            Enqueue("WaitlistPromoted", registration.Id, new { event_id = eventId, registration_id = registration.Id });
        }
    }

    private void Enqueue(string type, string subjectId, object payload)
    {
        var idempotencyKey = $"{subjectId}:{type}";
        if (db.NotificationJobs.Local.Any(job => job.IdempotencyKey == idempotencyKey))
        {
            return;
        }

        db.NotificationJobs.Add(new NotificationJob
        {
            Id = NewId(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload),
            IdempotencyKey = idempotencyKey,
        });
    }

    private static UserRecord ToRecord(User user) =>
        new(user.Id, user.Email, user.PasswordHash, user.Role, user.DisplayName);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
