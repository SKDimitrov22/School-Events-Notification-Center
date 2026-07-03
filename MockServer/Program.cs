using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var urls = builder.Configuration["ASPNETCORE_URLS"]
    ?? builder.Configuration["urls"]
    ?? "http://localhost:5090";
builder.WebHost.UseUrls(urls);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();

var database = new MockDatabase(app.Environment.ContentRootPath);
database.EnsureCreated();

app.MapGet("/health", () => Results.Ok(new { status = "ok", storage = "sqlite" }));

app.MapPost("/login", IResult (LoginRequest request) =>
{
    var user = database.GetUserByEmail(request.Email);
    if (user is null)
    {
        return Results.NotFound(new { error = "User not found." });
    }

    return Results.Ok(new LoginResponse(MockDatabase.GenerateJwt(user), user));
});

app.MapPost("/signup", IResult (SignupRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password) ||
        string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Role))
    {
        return Results.BadRequest(new { error = "All fields are required." });
    }

    if (database.GetUserByEmail(request.Email) is not null)
    {
        return Results.Conflict(new { error = "Email already registered." });
    }

    var role = request.Role.Trim().ToUpperInvariant() switch
    {
        "ORGANIZER" => "ORGANIZER",
        _ => "STUDENT",
    };

    var user = database.CreateUser(request.Email.Trim(), request.Password.Trim(), role, request.DisplayName.Trim());
    return user is null ? Results.BadRequest(new { error = "Could not create user." }) : Results.Ok(user);
});

app.MapGet("/events", (HttpRequest request) =>
{
    var user = database.GetCurrentUser(request);
    return Results.Ok(database.GetEvents(user));
});

app.MapGet("/events/{id}", IResult (string id, HttpRequest request) =>
{
    var user = database.GetCurrentUser(request);
    var eventSummary = database.GetEvent(id, user);
    return eventSummary is null ? Results.NotFound() : Results.Ok(eventSummary);
});

app.MapPost("/events", IResult (EventUpsertRequest input, HttpRequest request) =>
{
    var user = database.RequireRole(request, "ORGANIZER");
    return user is null
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(database.CreateEvent(user.Id, input));
});

app.MapPut("/events/{id}", IResult (string id, EventUpsertRequest input, HttpRequest request) =>
{
    var user = database.RequireRole(request, "ORGANIZER");
    if (user is null)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var updated = database.UpdateEvent(id, user.Id, input);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

app.MapPost("/events/{id}/publish", IResult (string id, HttpRequest request) =>
{
    var user = database.RequireRole(request, "ORGANIZER");
    if (user is null)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var updated = database.SetEventStatus(id, user.Id, "PUBLISHED");
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

app.MapPost("/events/{id}/cancel", IResult (string id, HttpRequest request) =>
{
    var user = database.RequireRole(request, "ORGANIZER");
    if (user is null)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var updated = database.SetEventStatus(id, user.Id, "CANCELLED");
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

app.MapPost("/events/{id}/registrations", IResult (string id, HttpRequest request) =>
{
    var user = database.RequireRole(request, "STUDENT");
    if (user is null)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var registration = database.Register(id, user.Id);
    return registration is null
        ? Results.BadRequest(new { error = "Registration could not be created." })
        : Results.Ok(registration);
});

app.MapDelete("/registrations/{id}", IResult (string id, HttpRequest request) =>
{
    var user = database.RequireRole(request, "STUDENT");
    if (user is null)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return database.CancelRegistration(id, user.Id) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/registrations/me", IResult (HttpRequest request) =>
{
    var user = database.RequireRole(request, "STUDENT");
    return user is null
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(database.GetRegistrationsForUser(user.Id));
});

app.MapGet("/events/{id}/registrations", IResult (string id, HttpRequest request) =>
{
    var user = database.RequireRole(request, "ORGANIZER");
    return user is null
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(database.GetRegistrationsForEvent(id, user.Id, "CONFIRMED"));
});

app.MapGet("/events/{id}/waitlist", IResult (string id, HttpRequest request) =>
{
    var user = database.RequireRole(request, "ORGANIZER");
    return user is null
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(database.GetRegistrationsForEvent(id, user.Id, "WAITLISTED"));
});

app.Run();

sealed class MockDatabase
{
    private readonly string connectionString;
    private readonly string contentRoot;

    public MockDatabase(string contentRoot)
    {
        this.contentRoot = contentRoot;
        var dataDir = Path.Combine(contentRoot, "App_Data");
        Directory.CreateDirectory(dataDir);
        connectionString = $"Data Source={Path.Combine(dataDir, "mock-school-events.db")}";
    }

    public void EnsureCreated()
    {
        using var connection = OpenConnection();
        var count = Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'users'");
        if (count == 0)
        {
            var publishedSchemaPath = Path.Combine(contentRoot, "Db", "schema.sql");
            var sourceTreeSchemaPath = Path.GetFullPath(Path.Combine(contentRoot, "..", "DataAccessLayer3", "Db", "schema.sql"));
            var schemaPath = File.Exists(publishedSchemaPath) ? publishedSchemaPath : sourceTreeSchemaPath;
            using var schema = connection.CreateCommand();
            schema.CommandText = File.ReadAllText(schemaPath);
            schema.ExecuteNonQuery();
        }

        if (Scalar<long>(connection, "SELECT COUNT(*) FROM users") == 0)
        {
            Seed(connection);
        }
    }

    public MockUser? GetCurrentUser(HttpRequest request)
    {
        var sub = JwtSub(request.Headers.Authorization.FirstOrDefault());
        return sub is null ? null : GetUserById(sub);
    }

    public MockUser? RequireRole(HttpRequest request, string role)
    {
        var user = GetCurrentUser(request);
        return string.Equals(user?.Role, role, StringComparison.OrdinalIgnoreCase) ? user : null;
    }

    public MockUser? GetUserByEmail(string email)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, email, role, display_name FROM users WHERE email = $email";
        command.Parameters.AddWithValue("$email", email);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public MockUser? GetUserById(string id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, email, role, display_name FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public MockUser? CreateUser(string email, string password, string role, string displayName)
    {
        var id = NewId();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO users (id, email, password_hash, role, display_name) VALUES ($id, $email, $password, $role, $displayName)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$password", password);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$displayName", displayName);

        try
        {
            command.ExecuteNonQuery();
        }
        catch
        {
            return null;
        }

        return new MockUser(id, email, role, displayName);
    }

    public IReadOnlyList<EventSummary> GetEvents(MockUser? user)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (string.Equals(user?.Role, "ORGANIZER", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = "SELECT * FROM events WHERE organizer_id = $organizerId ORDER BY starts_at";
            command.Parameters.AddWithValue("$organizerId", user.Id);
        }
        else
        {
            command.CommandText = "SELECT * FROM events WHERE status = 'PUBLISHED' ORDER BY starts_at";
        }

        using var reader = command.ExecuteReader();
        var events = new List<EventSummary>();
        while (reader.Read())
        {
            events.Add(ReadEventSummary(connection, reader, user));
        }

        return events;
    }

    public EventSummary? GetEvent(string id, MockUser? user)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM events WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var organizerId = reader.GetString(reader.GetOrdinal("organizer_id"));
        var status = reader.GetString(reader.GetOrdinal("status"));
        var canSee = status == "PUBLISHED" ||
            string.Equals(user?.Role, "ORGANIZER", StringComparison.OrdinalIgnoreCase) && user.Id == organizerId;

        return canSee ? ReadEventSummary(connection, reader, user) : null;
    }

    public EventSummary CreateEvent(string organizerId, EventUpsertRequest input)
    {
        using var connection = OpenConnection();
        var id = NewId();
        Execute(
            connection,
            """
            INSERT INTO events (id, organizer_id, title, description, starts_at, ends_at, capacity, location, status)
            VALUES ($id, $organizerId, $title, $description, $startsAt, $endsAt, $capacity, $location, 'DRAFT')
            """,
            ("$id", id),
            ("$organizerId", organizerId),
            ("$title", input.Title),
            ("$description", input.Description),
            ("$startsAt", input.StartsAt),
            ("$endsAt", input.EndsAt),
            ("$capacity", input.Capacity),
            ("$location", (object?)input.Location ?? DBNull.Value));

        return GetEvent(id, GetUserById(organizerId))!;
    }

    public EventSummary? UpdateEvent(string id, string organizerId, EventUpsertRequest input)
    {
        using var connection = OpenConnection();
        var rows = Execute(
            connection,
            """
            UPDATE events
            SET title = $title,
                description = $description,
                starts_at = $startsAt,
                ends_at = $endsAt,
                capacity = $capacity,
                location = $location,
                updated_at = datetime('now')
            WHERE id = $id AND organizer_id = $organizerId AND status != 'CANCELLED'
            """,
            ("$id", id),
            ("$organizerId", organizerId),
            ("$title", input.Title),
            ("$description", input.Description),
            ("$startsAt", input.StartsAt),
            ("$endsAt", input.EndsAt),
            ("$capacity", input.Capacity),
            ("$location", (object?)input.Location ?? DBNull.Value));

        return rows == 0 ? null : GetEvent(id, GetUserById(organizerId));
    }

    public EventSummary? SetEventStatus(string id, string organizerId, string status)
    {
        using var connection = OpenConnection();
        var rows = Execute(
            connection,
            "UPDATE events SET status = $status, updated_at = datetime('now') WHERE id = $id AND organizer_id = $organizerId",
            ("$id", id),
            ("$organizerId", organizerId),
            ("$status", status));

        if (rows > 0 && status == "CANCELLED")
        {
            Enqueue(connection, "EventCancelled", id, new { event_id = id });
        }

        return rows == 0 ? null : GetEvent(id, GetUserById(organizerId));
    }

    public RegistrationSummary? Register(string eventId, string userId)
    {
        using var connection = OpenConnection();
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var status = Scalar<string?>(connection, "SELECT status FROM events WHERE id = $id", ("$id", eventId));
            if (status != "PUBLISHED")
            {
                Execute(connection, "ROLLBACK");
                return null;
            }

            var active = Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM registrations WHERE event_id = $eventId AND user_id = $userId AND status IN ('CONFIRMED', 'WAITLISTED')",
                ("$eventId", eventId),
                ("$userId", userId));
            if (active > 0)
            {
                Execute(connection, "ROLLBACK");
                return null;
            }

            var capacity = Scalar<long>(connection, "SELECT capacity FROM events WHERE id = $id", ("$id", eventId));
            var confirmed = Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM registrations WHERE event_id = $eventId AND status = 'CONFIRMED'",
                ("$eventId", eventId));
            var registrationStatus = confirmed < capacity ? "CONFIRMED" : "WAITLISTED";
            var registrationId = NewId();

            Execute(
                connection,
                "INSERT INTO registrations (id, event_id, user_id, status) VALUES ($id, $eventId, $userId, $status)",
                ("$id", registrationId),
                ("$eventId", eventId),
                ("$userId", userId),
                ("$status", registrationStatus));

            Enqueue(
                connection,
                registrationStatus == "CONFIRMED" ? "RegistrationConfirmed" : "RegistrationWaitlisted",
                registrationId,
                new { event_id = eventId, user_id = userId, registration_id = registrationId });

            Execute(connection, "COMMIT");
            return GetRegistration(registrationId);
        }
        catch
        {
            Execute(connection, "ROLLBACK");
            throw;
        }
    }

    public bool CancelRegistration(string registrationId, string userId)
    {
        using var connection = OpenConnection();
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            using var find = connection.CreateCommand();
            find.CommandText = "SELECT event_id, status FROM registrations WHERE id = $id AND user_id = $userId AND status IN ('CONFIRMED', 'WAITLISTED')";
            find.Parameters.AddWithValue("$id", registrationId);
            find.Parameters.AddWithValue("$userId", userId);
            using var reader = find.ExecuteReader();
            if (!reader.Read())
            {
                Execute(connection, "ROLLBACK");
                return false;
            }

            var eventId = reader.GetString(0);
            var oldStatus = reader.GetString(1);
            reader.Close();

            Execute(
                connection,
                "UPDATE registrations SET status = 'CANCELLED', cancelled_at = datetime('now') WHERE id = $id",
                ("$id", registrationId));
            Enqueue(connection, "RegistrationCancelled", registrationId, new { event_id = eventId, user_id = userId, registration_id = registrationId });

            if (oldStatus == "CONFIRMED")
            {
                var nextId = Scalar<string?>(
                    connection,
                    "SELECT id FROM registrations WHERE event_id = $eventId AND status = 'WAITLISTED' ORDER BY registered_at, id LIMIT 1",
                    ("$eventId", eventId));
                if (nextId is not null)
                {
                    Execute(connection, "UPDATE registrations SET status = 'CONFIRMED' WHERE id = $id", ("$id", nextId));
                    Enqueue(connection, "WaitlistPromoted", nextId, new { event_id = eventId, registration_id = nextId });
                }
            }

            Execute(connection, "COMMIT");
            return true;
        }
        catch
        {
            Execute(connection, "ROLLBACK");
            throw;
        }
    }

    public IReadOnlyList<RegistrationSummary> GetRegistrationsForUser(string userId)
    {
        using var connection = OpenConnection();
        return ReadRegistrations(
            connection,
            """
            SELECT r.*, e.title AS event_title, u.display_name, u.email
            FROM registrations r
            JOIN events e ON e.id = r.event_id
            JOIN users u ON u.id = r.user_id
            WHERE r.user_id = $userId AND r.status IN ('CONFIRMED', 'WAITLISTED')
            ORDER BY e.starts_at
            """,
            ("$userId", userId));
    }

    public IReadOnlyList<RegistrationSummary> GetRegistrationsForEvent(string eventId, string organizerId, string status)
    {
        using var connection = OpenConnection();
        var ownsEvent = Scalar<long>(
            connection,
            "SELECT COUNT(*) FROM events WHERE id = $eventId AND organizer_id = $organizerId",
            ("$eventId", eventId),
            ("$organizerId", organizerId)) > 0;
        if (!ownsEvent)
        {
            return [];
        }

        return ReadRegistrations(
            connection,
            """
            SELECT r.*, e.title AS event_title, u.display_name, u.email
            FROM registrations r
            JOIN events e ON e.id = r.event_id
            JOIN users u ON u.id = r.user_id
            WHERE r.event_id = $eventId AND r.status = $status
            ORDER BY r.registered_at, r.id
            """,
            ("$eventId", eventId),
            ("$status", status));
    }

    private RegistrationSummary? GetRegistration(string id)
    {
        using var connection = OpenConnection();
        return ReadRegistrations(
            connection,
            """
            SELECT r.*, e.title AS event_title, u.display_name, u.email
            FROM registrations r
            JOIN events e ON e.id = r.event_id
            JOIN users u ON u.id = r.user_id
            WHERE r.id = $id
            """,
            ("$id", id)).FirstOrDefault();
    }

    private IReadOnlyList<RegistrationSummary> ReadRegistrations(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        var registrations = new List<RegistrationSummary>();
        while (reader.Read())
        {
            var eventId = reader.GetString(reader.GetOrdinal("event_id"));
            var registrationId = reader.GetString(reader.GetOrdinal("id"));
            var status = reader.GetString(reader.GetOrdinal("status"));
            int? waitlistPosition = null;
            if (status == "WAITLISTED")
            {
                using var positionConnection = OpenConnection();
                waitlistPosition = WaitlistPosition(positionConnection, eventId, registrationId);
            }

            registrations.Add(new RegistrationSummary(
                registrationId,
                eventId,
                reader.GetString(reader.GetOrdinal("event_title")),
                reader.GetString(reader.GetOrdinal("user_id")),
                reader.GetString(reader.GetOrdinal("display_name")),
                reader.GetString(reader.GetOrdinal("email")),
                status,
                reader.GetString(reader.GetOrdinal("registered_at")),
                waitlistPosition));
        }

        return registrations;
    }

    private EventSummary ReadEventSummary(SqliteConnection connection, SqliteDataReader reader, MockUser? user)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var organizerId = reader.GetString(reader.GetOrdinal("organizer_id"));
        var title = reader.GetString(reader.GetOrdinal("title"));
        var description = reader.GetString(reader.GetOrdinal("description"));
        var startsAt = reader.GetString(reader.GetOrdinal("starts_at"));
        var endsAt = reader.GetString(reader.GetOrdinal("ends_at"));
        var capacity = reader.GetInt32(reader.GetOrdinal("capacity"));
        var location = reader.IsDBNull(reader.GetOrdinal("location")) ? null : reader.GetString(reader.GetOrdinal("location"));
        var status = reader.GetString(reader.GetOrdinal("status"));
        using var summaryConnection = OpenConnection();
        var confirmed = (int)Scalar<long>(
            summaryConnection,
            "SELECT COUNT(*) FROM registrations WHERE event_id = $eventId AND status = 'CONFIRMED'",
            ("$eventId", id));
        var waitlist = (int)Scalar<long>(
            summaryConnection,
            "SELECT COUNT(*) FROM registrations WHERE event_id = $eventId AND status = 'WAITLISTED'",
            ("$eventId", id));

        string? registrationId = null;
        string? registrationStatus = null;
        int? waitlistPosition = null;
        if (string.Equals(user?.Role, "STUDENT", StringComparison.OrdinalIgnoreCase))
        {
            using var registration = summaryConnection.CreateCommand();
            registration.CommandText = "SELECT id, status FROM registrations WHERE event_id = $eventId AND user_id = $userId AND status IN ('CONFIRMED', 'WAITLISTED')";
            registration.Parameters.AddWithValue("$eventId", id);
            registration.Parameters.AddWithValue("$userId", user.Id);
            using var registrationReader = registration.ExecuteReader();
            if (registrationReader.Read())
            {
                registrationId = registrationReader.GetString(0);
                registrationStatus = registrationReader.GetString(1);
                waitlistPosition = registrationStatus == "WAITLISTED" ? WaitlistPosition(summaryConnection, id, registrationId) : null;
            }
        }

        return new EventSummary(
            id,
            organizerId,
            title,
            description,
            startsAt,
            endsAt,
            capacity,
            location,
            status,
            confirmed,
            waitlist,
            confirmed >= capacity,
            registrationId,
            registrationStatus,
            waitlistPosition);
    }

    private int WaitlistPosition(SqliteConnection connection, string eventId, string registrationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) + 1
            FROM registrations ahead
            JOIN registrations current ON current.id = $registrationId
            WHERE ahead.event_id = $eventId
              AND ahead.status = 'WAITLISTED'
              AND (ahead.registered_at < current.registered_at
                   OR (ahead.registered_at = current.registered_at AND ahead.id < current.id))
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$registrationId", registrationId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void Enqueue(SqliteConnection connection, string type, string subjectId, object payload)
    {
        Execute(
            connection,
            """
            INSERT OR IGNORE INTO notification_jobs (id, type, payload, idempotency_key)
            VALUES ($id, $type, $payload, $idempotencyKey)
            """,
            ("$id", NewId()),
            ("$type", type),
            ("$payload", JsonSerializer.Serialize(payload)),
            ("$idempotencyKey", $"{subjectId}:{type}"));
    }

    private void Seed(SqliteConnection connection)
    {
        Execute(
            connection,
            """
            INSERT INTO users (id, email, password_hash, role, display_name)
            VALUES
                ('organizer-1', 'organizer1@school.local', 'mock', 'ORGANIZER', 'Mira Organizer'),
                ('student-1', 'student1@school.local', 'mock', 'STUDENT', 'Alex Student'),
                ('student-2', 'student2@school.local', 'mock', 'STUDENT', 'Nina Student'),
                ('student-3', 'student3@school.local', 'mock', 'STUDENT', 'Theo Student')
            """);

        Execute(
            connection,
            """
            INSERT INTO events (id, organizer_id, title, description, starts_at, ends_at, capacity, location, status)
            VALUES
                ('event-robotics', 'organizer-1', 'Robotics Workshop', 'Build and program small robots in teams.', '2026-07-03T14:00:00Z', '2026-07-03T16:00:00Z', 2, 'Lab 2', 'PUBLISHED'),
                ('event-literature', 'organizer-1', 'Literature Club', 'Discuss short stories and prepare readings.', '2026-07-08T13:00:00Z', '2026-07-08T14:30:00Z', 12, 'Library', 'PUBLISHED'),
                ('event-draft', 'organizer-1', 'Chemistry Demo Day', 'Draft event for organizer editing and preview.', '2026-07-15T10:00:00Z', '2026-07-15T12:00:00Z', 18, 'Science Hall', 'DRAFT')
            """);

        Execute(
            connection,
            """
            INSERT INTO registrations (id, event_id, user_id, status, registered_at)
            VALUES
                ('reg-robotics-1', 'event-robotics', 'student-2', 'CONFIRMED', '2026-06-20T08:00:00Z'),
                ('reg-robotics-2', 'event-robotics', 'student-3', 'CONFIRMED', '2026-06-20T08:05:00Z'),
                ('reg-robotics-3', 'event-robotics', 'student-1', 'WAITLISTED', '2026-06-20T08:10:00Z')
            """);

        Enqueue(connection, "RegistrationConfirmed", "reg-robotics-1", new { event_id = "event-robotics", user_id = "student-2", registration_id = "reg-robotics-1" });
        Enqueue(connection, "RegistrationConfirmed", "reg-robotics-2", new { event_id = "event-robotics", user_id = "student-3", registration_id = "reg-robotics-2" });
        Enqueue(connection, "RegistrationWaitlisted", "reg-robotics-3", new { event_id = "event-robotics", user_id = "student-1", registration_id = "reg-robotics-3" });
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON");
        return connection;
    }

    private static MockUser ReadUser(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("email")),
            reader.GetString(reader.GetOrdinal("role")),
            reader.GetString(reader.GetOrdinal("display_name")));

    private static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    public static string GenerateJwt(MockUser user)
    {
        var header = B64U("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = B64U(JsonSerializer.Serialize(new
        {
            sub = user.Id, email = user.Email, role = user.Role, name = user.DisplayName,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()
        }));
        return $"{header}.{payload}.";
    }

    private static string B64U(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');

    private static string? JwtSub(string? authHeader)
    {
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ")) return null;
        var parts = authHeader["Bearer ".Length..].Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var pad = parts[1].Replace('-', '+').Replace('_', '/');
            pad = pad.PadRight(pad.Length + (4 - pad.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(pad)));
            return doc.RootElement.GetProperty("sub").GetString();
        }
        catch { return null; }
    }
}

sealed record MockUser(string Id, string Email, string Role, string DisplayName);
sealed record LoginRequest(string Email, string Password = "");
sealed record SignupRequest(string Email, string Password, string Role, string DisplayName);
sealed record LoginResponse(string Token, MockUser User);
sealed class EventUpsertRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string StartsAt { get; set; } = string.Empty;
    public string EndsAt { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public string? Location { get; set; }
}
sealed record EventSummary(
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
sealed record RegistrationSummary(
    string Id,
    string EventId,
    string EventTitle,
    string UserId,
    string UserDisplayName,
    string UserEmail,
    string Status,
    string RegisteredAt,
    int? WaitlistPosition);
