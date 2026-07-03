using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PresentationLayer1.Models;
using PresentationLayer1.Services;

namespace PresentationLayer1.Pages;

public sealed class SignupModel(IApiClient api, IAuthSession auth) : PageModel
{
    [BindProperty]
    public SignupRequest Input { get; set; } = new SignupRequest();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var success = await api.SignUpAsync(Input, cancellationToken);
        if (!success)
        {
            ModelState.AddModelError(string.Empty, "Unable to create account. Email may already be used.");
            return Page();
        }

        var login = await api.LoginAsync(Input.Email, Input.Password, cancellationToken);
        if (login is not null)
        {
            auth.SignIn(login);
            TempData["Message"] = $"Signed in as {login.User.DisplayName}.";
            return string.Equals(login.User.Role, "ORGANIZER", StringComparison.OrdinalIgnoreCase)
                ? RedirectToPage("/Organizer/Events/Index")
                : RedirectToPage("/Events/Index");
        }

        TempData["Message"] = "Account created. You can now log in.";
        return RedirectToPage("/Login");
    }
}
