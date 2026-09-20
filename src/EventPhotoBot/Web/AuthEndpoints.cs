using System.Threading.RateLimiting;

namespace EventPhotoBot.Web;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static void AddLoginRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 8,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

    public static void MapAuth(this WebApplication app, AppConfig config)
    {
        app.MapGet("/login", () => Results.File(
            Path.Combine(app.Environment.WebRootPath, "login.html"), "text/html"));

        app.MapPost("/login", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            if (!SessionCookie.PasswordMatches(config.AdminPassword, form["password"]))
                return Results.Redirect("/login?error=bad");

            var expiresAt = DateTimeOffset.UtcNow.Add(SessionCookie.Lifetime);
            http.Response.Cookies.Append(
                SessionCookie.Name,
                SessionCookie.Issue(config.CookieSigningKey, expiresAt),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = expiresAt,
                    Path = "/",
                });
            return Results.Redirect("/show");
        }).RequireRateLimiting(LoginRateLimitPolicy);

        app.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(SessionCookie.Name);
            return Results.Redirect("/login");
        });
    }

    /// <summary>
    /// The gate. Everything except the webhook, the health probe and the login
    /// surface needs a session; API calls get 401, pages get the login form.
    /// </summary>
    public static void UseSessionGate(this WebApplication app, AppConfig config)
    {
        var open = new HashSet<string>(StringComparer.Ordinal)
        {
            "/healthz", "/login", $"/tg/{config.WebhookPath}",
        };

        app.Use(async (http, next) =>
        {
            var path = http.Request.Path.Value ?? "/";
            if (open.Contains(path))
            {
                await next();
                return;
            }

            var cookie = http.Request.Cookies[SessionCookie.Name];
            if (SessionCookie.IsValid(config.CookieSigningKey, cookie, DateTimeOffset.UtcNow))
            {
                await next();
                return;
            }

            if (path.StartsWith("/api/", StringComparison.Ordinal)
                || path.StartsWith("/img/", StringComparison.Ordinal))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            http.Response.Redirect("/login");
        });
    }
}
