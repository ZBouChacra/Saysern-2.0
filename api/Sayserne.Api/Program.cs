using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Sayserne.Api;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Endpoints;
using Sayserne.Api.Infrastructure;

var app = AppFactory.Build(args);
app.Run();

namespace Sayserne.Api
{
    public static class AppFactory
    {
        /// <summary>Builds the API. Tests call this with their own configuration and replacement services.</summary>
        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null, string? environment = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = environment });
            configure?.Invoke(builder);

            var connectionString = builder.Configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ConnectionStrings:Default is not configured. Set the ConnectionStrings__Default environment variable (see README).");

            var csb = new MySqlConnectionStringBuilder(connectionString) { DateTimeKind = MySqlDateTimeKind.Utc, AllowUserVariables = false };
            builder.Services.AddSingleton(new MySqlDataSource(csb.ConnectionString));
            builder.Services.AddSingleton<IStoredProcedureExecutor, StoredProcedureExecutor>();
            builder.Services.AddSingleton<IUserRepository, UserRepository>();
            builder.Services.AddSingleton<IIdentityRepository, IdentityRepository>();
            builder.Services.AddSingleton<IAdminUserRepository, AdminUserRepository>();

            builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
            builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection("Google"));
            var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(auth.PasswordIterations));
            builder.Services.AddSingleton<DummyPasswordHash>();
            builder.Services.AddSingleton<ProtectedCookies>();
            builder.Services.AddSingleton<IGoogleIdTokenValidator, GoogleIdTokenValidator>();
            builder.Services.AddHttpClient("google", c => c.Timeout = TimeSpan.FromSeconds(10));

            var dp = builder.Services.AddDataProtection().SetApplicationName("Sayserne");
            if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
                dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));   // share this folder across instances

            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(o =>
                {
                    o.Cookie.Name = "sayserne.auth";
                    o.Cookie.HttpOnly = true;
                    o.Cookie.SameSite = SameSiteMode.Lax;
                    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                    o.ExpireTimeSpan = TimeSpan.FromHours(auth.SessionHours);
                    o.SlidingExpiration = true;
                    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
                    // Re-check the account on every request: locking a user or changing a role takes effect immediately.
                    o.Events.OnValidatePrincipal = async c =>
                    {
                        var id = AuthSession.CurrentUserId(c.Principal!);
                        var users = c.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                        var user = id is null ? null : await users.GetByIdAsync(id.Value);
                        if (user is null || user.IsLocked || user.Role != c.Principal!.FindFirstValue(ClaimTypes.Role))
                        {
                            c.RejectPrincipal();
                            await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    };
                });
            builder.Services.AddAuthorizationBuilder()
                .AddPolicy(AdminEndpoints.PolicyName, p => p.RequireAuthenticatedUser().RequireRole(AuthSession.RoleAdmin));

            builder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = auth.AuthRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });

            builder.Services.ConfigureHttpJsonOptions(o =>
                o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

            var app = builder.Build();

            if (app.Configuration.GetValue<bool>("Proxy:TrustForwardedHeaders"))
            {
                var fh = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
                fh.KnownIPNetworks.Clear();
                fh.KnownProxies.Clear();
                app.UseForwardedHeaders(fh);   // only enable when the API is reachable exclusively through your reverse proxy
            }

            app.UseMiddleware<SecurityHeadersMiddleware>();
            app.UseMiddleware<ErrorHandlingMiddleware>();
            app.UseRateLimiter();
            app.UseMiddleware<CsrfMiddleware>();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapAuth();
            app.MapGoogle();
            app.MapAdmin();
            return app;
        }
    }
}
