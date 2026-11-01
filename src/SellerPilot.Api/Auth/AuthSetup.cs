using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

namespace SellerPilot.Api.Auth;

public static class AuthRoles
{
    public const string Seller = "seller";
    public const string Customer = "customer";
}

public static class AuthPolicies
{
    /// <summary>The back office: chat, approvals, orders, reconciliation, audit, eval.</summary>
    public const string Seller = "Seller";

    /// <summary>The storefront: a customer's own orders, chat and refund requests.</summary>
    public const string Customer = "Customer";

    public const string LoginRateLimit = "login";
}

public static class AuthClaims
{
    /// <summary>The signed-in customer's ID, or null for the seller and anonymous users.</summary>
    public static int? CustomerId(this ClaimsPrincipal user) =>
        user.IsInRole(AuthRoles.Customer) && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}

public static class AuthSetup
{
    /// <summary>
    /// Cookie sign-in for the demo seller and customers. The cookie is HttpOnly and SameSite=Strict (the API only accepts JSON,
    /// so cross-site forms can't post to it); unauthenticated API calls get 401/403 instead of a login redirect.
    /// </summary>
    public static IServiceCollection AddSellerPilotAuth(this IServiceCollection services)
    {
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "sellerpilot.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Seller, p => p.RequireAuthenticatedUser().RequireRole(AuthRoles.Seller))
            .AddPolicy(AuthPolicies.Customer, p => p.RequireAuthenticatedUser().RequireRole(AuthRoles.Customer));

        // Slows down password guessing: 10 sign-in attempts per minute per client address.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthPolicies.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }
}
