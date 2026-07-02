using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PresentationLayer1.Models;
using PresentationLayer1.Services;

namespace PresentationLayer1.Pages.Organizer.Events;

public sealed class IndexModel(IApiClient api, IAuthSession auth) : PageModel
{
    public IReadOnlyList<EventSummary> Events { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!auth.IsOrganizer)
        {
            TempData["Error"] = "Sign in as an organizer to manage events.";
            return RedirectToPage("/Login");
        }

        Events = await api.GetEventsAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostPublishAsync(string id, CancellationToken cancellationToken)
    {
        if (!auth.IsOrganizer)
        {
            TempData["Error"] = "Sign in as an organizer to publish events.";
            return RedirectToPage("/Login");
        }

        try
        {
            await api.PublishEventAsync(id, cancellationToken);
            TempData["Message"] = "Event published.";
        }
        catch (ApiException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelAsync(string id, CancellationToken cancellationToken)
    {
        if (!auth.IsOrganizer)
        {
            TempData["Error"] = "Sign in as an organizer to cancel events.";
            return RedirectToPage("/Login");
        }

        try
        {
            await api.CancelEventAsync(id, cancellationToken);
            TempData["Message"] = "Event cancelled.";
        }
        catch (ApiException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage();
    }
}
