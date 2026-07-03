using System.Net;
using System.Net.Http.Json;
using PresentationLayer1.Models;

namespace PresentationLayer1.Services;

/// <summary>
/// Raised when the backend rejects a request. Carries a message that is safe to
/// show to the user so page handlers can surface it instead of crashing with a 500.
/// </summary>
public sealed class ApiException(string message) : Exception(message);

public interface IApiClient
{
    Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<bool> SignUpAsync(SignupRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EventSummary>> GetEventsAsync(CancellationToken cancellationToken = default);
    Task<EventSummary?> GetEventAsync(string id, CancellationToken cancellationToken = default);
    Task<EventSummary?> CreateEventAsync(EventUpsertRequest request, CancellationToken cancellationToken = default);
    Task<EventSummary?> UpdateEventAsync(string id, EventUpsertRequest request, CancellationToken cancellationToken = default);
    Task<EventSummary?> PublishEventAsync(string id, CancellationToken cancellationToken = default);
    Task<EventSummary?> CancelEventAsync(string id, CancellationToken cancellationToken = default);
    Task<RegistrationSummary?> RegisterAsync(string eventId, CancellationToken cancellationToken = default);
    Task CancelRegistrationAsync(string registrationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistrationSummary>> GetMyRegistrationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistrationSummary>> GetConfirmedRegistrationsAsync(string eventId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistrationSummary>> GetWaitlistAsync(string eventId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationLogSummary>> GetNotificationLogsAsync(CancellationToken cancellationToken = default);
}

public sealed class ApiClient(HttpClient httpClient, IAuthSession authSession) : IApiClient
{
    public async Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = NewRequest(HttpMethod.Post, "login");
        request.Content = JsonContent.Create(new LoginRequest(email, password));
        using var response = await httpClient.SendAsync(request, cancellationToken);

        // Bad credentials are an expected outcome, not an error: let the caller
        // decide how to phrase it (mock vs. real login) by returning null.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        using var request = NewRequest(HttpMethod.Post, "api/login");
        request.Content = JsonContent.Create(new LoginRequest(email, password));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
    }

    public Task<IReadOnlyList<EventSummary>> GetEventsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<EventSummary>("api/events", cancellationToken);

    public Task<EventSummary?> GetEventAsync(string id, CancellationToken cancellationToken = default) =>
        GetAsync<EventSummary>($"api/events/{Uri.EscapeDataString(id)}", cancellationToken);

    public Task<EventSummary?> CreateEventAsync(EventUpsertRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<EventUpsertRequest, EventSummary>("api/events", request, cancellationToken);

    public Task<EventSummary?> UpdateEventAsync(string id, EventUpsertRequest request, CancellationToken cancellationToken = default) =>
        PutAsync<EventUpsertRequest, EventSummary>($"api/events/{Uri.EscapeDataString(id)}", request, cancellationToken);

    public Task<EventSummary?> PublishEventAsync(string id, CancellationToken cancellationToken = default) =>
        PostAsync<object, EventSummary>($"api/events/{Uri.EscapeDataString(id)}/publish", new { }, cancellationToken);

    public Task<EventSummary?> CancelEventAsync(string id, CancellationToken cancellationToken = default) =>
        PostAsync<object, EventSummary>($"api/events/{Uri.EscapeDataString(id)}/cancel", new { }, cancellationToken);

    public Task<RegistrationSummary?> RegisterAsync(string eventId, CancellationToken cancellationToken = default) =>
        PostAsync<object, RegistrationSummary>($"api/events/{Uri.EscapeDataString(eventId)}/registrations", new { }, cancellationToken);

    public async Task CancelRegistrationAsync(string registrationId, CancellationToken cancellationToken = default)
    {
        using var request = NewRequest(HttpMethod.Delete, $"api/registrations/{Uri.EscapeDataString(registrationId)}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<RegistrationSummary>> GetMyRegistrationsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<RegistrationSummary>("api/registrations/me", cancellationToken);

    public Task<IReadOnlyList<RegistrationSummary>> GetConfirmedRegistrationsAsync(string eventId, CancellationToken cancellationToken = default) =>
        GetListAsync<RegistrationSummary>($"api/events/{Uri.EscapeDataString(eventId)}/registrations", cancellationToken);

    public Task<IReadOnlyList<RegistrationSummary>> GetWaitlistAsync(string eventId, CancellationToken cancellationToken = default) =>
        GetListAsync<RegistrationSummary>($"api/events/{Uri.EscapeDataString(eventId)}/waitlist", cancellationToken);

    public Task<IReadOnlyList<NotificationLogSummary>> GetNotificationLogsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<NotificationLogSummary>("api/notifications", cancellationToken);

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken cancellationToken)
    {
        var value = await GetAsync<List<T>>(path, cancellationToken);
        return value ?? [];
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken);
    }

    private async Task<TResponse?> PutAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Put, path);
        request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Prefer the backend's own message (e.g. { "error": "..." }) when present.
        string? message = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken);
            message = string.IsNullOrWhiteSpace(body?.Error) ? null : body!.Error;
        }
        catch
        {
            // Body was empty or not JSON — fall back to a status-based message.
        }

        throw new ApiException(message ?? (response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Please sign in again.",
            HttpStatusCode.Forbidden => "You don't have permission to do that.",
            HttpStatusCode.NotFound => "That item no longer exists.",
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict => "That action isn't allowed right now.",
            _ => "Something went wrong. Please try again."
        }));
    }

    private sealed record ApiError(string? Error);

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(authSession.Token))
        {
            request.Headers.Add("Authorization", $"Bearer {authSession.Token}");
        }

        return request;
    }
}
