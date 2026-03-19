using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Endpoints;

public static class AuthenticationEndpoints
{
    public static WebApplication MapAuthenticationEndpoints(this WebApplication app)
    {
        app.MapLoginEndpoint()
            .MapLogoutEndpoint()
            .MapCheckAuthenticationEndpoint();

        return app;
    }

    private static WebApplication MapLoginEndpoint(this WebApplication app)
    {
        app.MapPost("/api/login", async (LoginRequest request,
            SimpleAuthService authService,
            SessionService sessionService,
            HttpContext context,
            ILogger<Program> logger) =>
            {
                logger.LogInformation("[AUTH LOGIN] Login attempt for user: {Username}", request.Username);

                var isValid = await authService.ValidateCredentialsAsync(request.Username, request.Password);

                if (isValid)
                {
                    logger.LogInformation("[AUTH LOGIN] Login successful for user: {Username}", request.Username);

                    // Get client IP and User-Agent for session tracking
                    var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var userAgent = context.Request.Headers["User-Agent"].ToString();

                    // Create secure session with cryptographic session ID
                    var sessionId = await sessionService.CreateSessionAsync(request.Username, ipAddress, userAgent, request.RememberMe);

                    // Set secure authentication cookie with session ID
                    var cookieOptions = new CookieOptions
                    {
                        HttpOnly = true, // Prevents JavaScript access (XSS protection)
                        Secure = false,  // Set to true in production with HTTPS
                        SameSite = SameSiteMode.Strict, // CSRF protection
                        Expires = request.RememberMe ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddDays(7),
                        Path = "/"
                    };
                    context.Response.Cookies.Append("SportarrAuth", sessionId, cookieOptions);

                    logger.LogInformation("[AUTH LOGIN] Session created from IP: {IP}", ipAddress);

                    return Results.Ok(new LoginResponse { Success = true, Token = sessionId, Message = "Login successful" });
                }

                logger.LogWarning("[AUTH LOGIN] Login failed for user: {Username}", request.Username);
                return Results.Unauthorized();
            }
        );

        return app;
    }

    private static WebApplication MapLogoutEndpoint(this WebApplication app)
    {
        app.MapPost("/api/logout", 
            async (SessionService sessionService,
                HttpContext context,
                ILogger<Program> logger) =>
            {
                logger.LogInformation("[AUTH LOGOUT] Logout requested");

                // Get session ID from cookie
                var sessionId = context.Request.Cookies["SportarrAuth"];
                if (!string.IsNullOrEmpty(sessionId))
                {
                    // Delete session from database
                    await sessionService.DeleteSessionAsync(sessionId);
                }

                // Delete cookie
                context.Response.Cookies.Delete("SportarrAuth");
                return Results.Ok(new { message = "Logged out successfully" });
            }
        );

        return app;
    }

    /// <summary>
    /// Auth check endpoint - determines if user needs to login
    /// Matches Sonarr/Radarr: no setup wizard, auth disabled by default</summary>
    /// <param name="app"></param>
    private static WebApplication MapCheckAuthenticationEndpoint(this WebApplication app)
    {
        app.MapGet("/api/auth/check", 
            async (SimpleAuthService authService,
                SessionService sessionService,
                HttpContext context,
                ILogger<Program> logger) =>
            {
                try
                {
                    logger.LogInformation("[AUTH CHECK] Starting auth check");

                    // Step 1: Check if authentication is required (based on settings)
                    var authMethod = await authService.GetAuthenticationMethodAsync();
                    var isAuthRequired = await authService.IsAuthenticationRequiredAsync();
                    logger.LogInformation("[AUTH CHECK] AuthMethod={AuthMethod}, IsAuthRequired={IsAuthRequired}", authMethod, isAuthRequired);

                    // If authentication is disabled (method = "none"), auto-authenticate
                    if (!isAuthRequired || authMethod == "none")
                    {
                        logger.LogInformation("[AUTH CHECK] Authentication disabled, auto-authenticating");
                        return Results.Ok(new { authenticated = true, authDisabled = true });
                    }

                    // If external auth, trust the proxy (user is authenticated externally)
                    if (authMethod == "external")
                    {
                        logger.LogInformation("[AUTH CHECK] External authentication enabled, trusting proxy");
                        return Results.Ok(new { authenticated = true, authMethod = "external" });
                    }

                    // Step 2: Authentication is required (forms or basic), validate session
                    var sessionId = context.Request.Cookies["SportarrAuth"];
                    if (string.IsNullOrEmpty(sessionId))
                    {
                        logger.LogInformation("[AUTH CHECK] No session cookie found");
                        return Results.Ok(new { authenticated = false });
                    }

                    // Get client IP and User-Agent for validation
                    var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var userAgent = context.Request.Headers["User-Agent"].ToString();

                    // Validate session with IP and User-Agent checks
                    var (isValid, username) = await sessionService.ValidateSessionAsync(
                        sessionId,
                        ipAddress,
                        userAgent,
                        strictIpCheck: true,      // Reject if IP doesn't match
                        strictUserAgentCheck: true // Reject if User-Agent doesn't match
                    );

                    if (isValid)
                    {
                        logger.LogInformation("[AUTH CHECK] Valid session for user {Username} from IP {IP}", username, ipAddress);
                        return Results.Ok(new { authenticated = true, username });
                    }
                    else
                    {
                        logger.LogWarning("[AUTH CHECK] Invalid session - IP or User-Agent mismatch");
                        // Delete invalid cookie
                        context.Response.Cookies.Delete("SportarrAuth");
                        return Results.Ok(new { authenticated = false });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[AUTH CHECK] CRITICAL ERROR: {Message}", ex.Message);
                    logger.LogError(ex, "[AUTH CHECK] Stack trace: {StackTrace}", ex.StackTrace);
                    // On error, assume authenticated to avoid blocking access (auth disabled by default)
                    return Results.Ok(new { authenticated = true, authDisabled = true });
                }
            }
        );

        return app;
    }
}
