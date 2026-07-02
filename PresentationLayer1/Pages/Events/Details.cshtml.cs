using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PresentationLayer1.Models;
using PresentationLayer1.Services;

namespace PresentationLayer1.Pages.Events;

public sealed class DetailsModel(IApiClient api, IAuthSession auth) : PageModel
{
    public EventSummary? Event { get; private set; }
    public IAuthSession Auth => auth;

    public async Task OnGetAsync(string id, CancellationToken cancellationToken)
    {
        Event = await api.GetEventAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnPostRegisterAsync(string id, CancellationToken cancellationToken)
    {
        if (!auth.IsStudent)
        {
            TempData["Error"] = "Sign in as a student to register.";
            return RedirectToPage("/Login");
        }

        try
        {
            var registration = await api.RegisterAsync(id, cancellationToken);
            if (registration is { Status: "WAITLISTED" })
            {
                TempData["Message"] = registration.WaitlistPosition is int position
                    ? $"The event is full — you're on the waitlist at position {position}."
                    : "The event is full — you've been added to the waitlist.";
            }
            else
            {
                TempData["Message"] = "You're registered — see you there!";
            }
        }
        catch (ApiException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage("/Events/Details", new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(string id, string registrationId, CancellationToken cancellationToken)
    {
        if (!auth.IsStudent)
        {
            TempData["Error"] = "Sign in as a student to cancel registrations.";
            return RedirectToPage("/Login");
        }

        try
        {
            await api.CancelRegistrationAsync(registrationId, cancellationToken);
            TempData["Message"] = "Registration cancelled.";
        }
        catch (ApiException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage("/Events/Details", new { id });
    }
}
