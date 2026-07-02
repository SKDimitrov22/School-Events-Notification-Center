using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PresentationLayer1.Models;
using PresentationLayer1.Services;

namespace PresentationLayer1.Pages.Registrations;

public sealed class MeModel(IApiClient api, IAuthSession auth) : PageModel
{
    public IReadOnlyList<RegistrationSummary> Registrations { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!auth.IsStudent)
        {
            TempData["Error"] = "Sign in as a student to view your signups.";
            return RedirectToPage("/Login");
        }

        Registrations = await api.GetMyRegistrationsAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(string registrationId, CancellationToken cancellationToken)
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

        return RedirectToPage();
    }
}
