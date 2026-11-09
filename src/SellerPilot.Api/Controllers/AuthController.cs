using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SellerPilot.Api.Audit;
using SellerPilot.Api.Auth;
using SellerPilot.Api.Configuration;
using SellerPilot.Api.Data;
using SellerPilot.Api.Guardrails;

namespace SellerPilot.Api.Controllers;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record CurrentUserDto(string Role, string Name, string Email);

public sealed record DemoLoginRequest(string? Email);

public sealed record DemoAccountDto(string Role, string Name, string Email);

/// <summary>The demo accounts for the sign-in page. <c>PublicDemo</c>: one-click sign-in without a password (hosted demo only).</summary>
public sealed record DemoInfoDto(bool PublicDemo, IReadOnlyList<DemoAccountDto> Accounts);

/// <summary>Demo sign-in: the seller (Emma) for the back office, customers for the storefront. One demo password from configuration.</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AppDbContext db, IAuditService audit, IOptions<ShopOptions> options) : ControllerBase
{
    private static readonly PasswordHasher<Customer> Hasher = new();

    [HttpPost("login")]
    [EnableRateLimiting(AuthPolicies.LoginRateLimit)]
    public async Task<ActionResult<CurrentUserDto>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var shop = options.Value;
        if (!shop.HasDemoPassword)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Sign-in is not configured. Set Shop__DemoPassword in .env (or user-secrets) and restart the API.");
        }

        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var password = request.Password ?? "";
        if (email.Length is 0 or > 200 || password.Length is 0 or > 200)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Email and password are required.");
        }

        CurrentUserDto? user = null;
        string? subject = null;
        if (string.Equals(email, shop.SellerEmail, StringComparison.OrdinalIgnoreCase))
        {
            if (FixedTimeEquals(password, shop.DemoPassword!))
            {
                user = new CurrentUserDto(AuthRoles.Seller, shop.SellerName, shop.SellerEmail);
                subject = "seller";
            }
        }
        else
        {
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Email == email, ct);
            if (customer?.PasswordHash is { } hash && Hasher.VerifyHashedPassword(customer, hash, password) != PasswordVerificationResult.Failed)
            {
                user = new CurrentUserDto(AuthRoles.Customer, customer.Name, customer.Email);
                subject = customer.Id.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (user is null)
        {
            await audit.LogAsync(AuditActors.System, AuditEvents.SignInFailed, new { email = Pii.MaskEmail(email) }, ct);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Wrong email or password.");
        }

        await SignInAsync(user, subject!, demo: false, ct);
        return user;
    }

    /// <summary>The demo accounts, so the sign-in page can list them. Demo data only; no secrets.</summary>
    [HttpGet("demo")]
    public async Task<DemoInfoDto> Demo(CancellationToken ct)
    {
        var shop = options.Value;
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new DemoAccountDto(AuthRoles.Customer, c.Name, c.Email)).ToListAsync(ct);
        return new DemoInfoDto(shop.PublicDemo, [new DemoAccountDto(AuthRoles.Seller, shop.SellerName, shop.SellerEmail), .. customers]);
    }

    /// <summary>Hosted public demo only: sign in as a demo account without a password. 404 everywhere else.</summary>
    [HttpPost("demo-login")]
    [EnableRateLimiting(AuthPolicies.LoginRateLimit)]
    public async Task<ActionResult<CurrentUserDto>> DemoLogin([FromBody] DemoLoginRequest request, CancellationToken ct)
    {
        var shop = options.Value;
        if (!shop.PublicDemo)
        {
            return NotFound();
        }

        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (string.Equals(email, shop.SellerEmail, StringComparison.OrdinalIgnoreCase))
        {
            var seller = new CurrentUserDto(AuthRoles.Seller, shop.SellerName, shop.SellerEmail);
            await SignInAsync(seller, "seller", demo: true, ct);
            return seller;
        }

        var customer = email.Length is 0 or > 200 ? null : await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Email == email, ct);
        if (customer is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown demo account.");
        }

        var user = new CurrentUserDto(AuthRoles.Customer, customer.Name, customer.Email);
        await SignInAsync(user, customer.Id.ToString(CultureInfo.InvariantCulture), demo: true, ct);
        return user;
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    public ActionResult<CurrentUserDto> Me()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in.");
        }

        return new CurrentUserDto(
            User.IsInRole(AuthRoles.Seller) ? AuthRoles.Seller : AuthRoles.Customer,
            User.FindFirstValue(ClaimTypes.Name) ?? "",
            User.FindFirstValue(ClaimTypes.Email) ?? "");
    }

    private async Task SignInAsync(CurrentUserDto user, string subject, bool demo, CancellationToken ct)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        await audit.LogAsync(user.Role == AuthRoles.Seller ? AuditActors.Seller : AuditActors.Customer, AuditEvents.SignedIn, new { role = user.Role, email = Pii.MaskEmail(user.Email), demo }, ct);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
