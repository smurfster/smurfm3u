using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Smurfm3u.App.Api;

/// <summary>
/// Sign-out only. Signing in happens on the login page, which is statically rendered and so
/// can write the cookie itself; signing out is a plain form post from the layout.
/// </summary>
[Route("account")]
public class AccountController(IAntiforgery antiforgery, ILogger<AccountController> logger) : ControllerBase
{
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        // [ValidateAntiForgeryToken] resolves an MVC *view* filter, which this app does not
        // register (AddControllers, not AddControllersWithViews), so validate explicitly.
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            // Almost always a stale token from a page left open. Send them to the login
            // page to get a fresh one rather than showing an error.
            logger.LogWarning(ex, "Rejected a sign-out with an invalid antiforgery token");
            return LocalRedirect("~/login");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("~/login");
    }
}
