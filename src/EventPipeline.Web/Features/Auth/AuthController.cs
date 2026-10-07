using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventPipeline.Web.Features.Auth;

/// <summary>
/// Cookie sign-in/sign-out for the web UI. The login page itself is a Blazor page
/// (GET /login); these endpoints only handle the POSTs. Antiforgery protects both
/// forms; the cookie carries just the username and the Admin role — the
/// must-change-password flag stays in the database.
/// </summary>
[Route("auth")]
public class AuthController : Controller
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
            || !await _authService.VerifyAsync(username, password, ct))
        {
            return Redirect("/login?error=1");
        }

        var user = await _authService.GetAsync(username, ct);
        if (user == null)
            return Redirect("/login?error=1");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, "Admin"),
        };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        return user.MustChangePassword ? Redirect("/ajustes") : Redirect("/");
    }

    [HttpPost("logout")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/login");
    }
}
