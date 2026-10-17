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
using Sayserne.Api.Email;
using Sayserne.Api.Infrastructure;
using Sayserne.Api.Ads;
using Sayserne.Api.Jobs;
using Sayserne.Api.Payments;
using Sayserne.Api.Premium;
using Sayserne.Api.Media;
using Sayserne.Api.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
            // registered with a factory so the container owns the pool and disposes it (an instance registration would never be disposed)
            builder.Services.AddSingleton(_ => new MySqlDataSource(csb.ConnectionString));
            builder.Services.AddSingleton<IStoredProcedureExecutor, StoredProcedureExecutor>();
            builder.Services.AddSingleton<IUserRepository, UserRepository>();
            builder.Services.AddSingleton<IIdentityRepository, IdentityRepository>();
            builder.Services.AddSingleton<IAdminUserRepository, AdminUserRepository>();
            builder.Services.AddSingleton<ICatalogRepository, CatalogRepository>();
            builder.Services.AddSingleton<IProfessionalRepository, ProfessionalRepository>();
            builder.Services.AddSingleton<IBookingRepository, BookingRepository>();
            builder.Services.AddSingleton<IMessagingRepository, MessagingRepository>();
            builder.Services.AddSingleton<IPlatformRepository, PlatformRepository>();
            builder.Services.AddSingleton<ITwoFactorRepository, TwoFactorRepository>();
            // runtime settings: loaded at startup, refreshed after admin edits and periodically (other instances)
            builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
            builder.Services.AddSingleton<ISettingsService>(sp => new SettingsService(sp.GetRequiredService<IStoredProcedureExecutor>(), sp.GetService<SettingOverrides>(), sp.GetRequiredService<ISecretProtector>()));
            builder.Services.AddSingleton<IEmailRepository, EmailRepository>();
            builder.Services.AddSingleton<IEmailRenderer, EmailRenderer>();
            builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
            builder.Services.AddSingleton<INotifier, Notifier>();
            builder.Services.AddSingleton<AccountEmails>();
            builder.Services.AddSingleton<MarketplaceNotifier>();
            builder.Services.AddSingleton<IJobHandler, EmailSendJobHandler>();
            builder.Services.AddSingleton<IJobHandler, MessageNotificationJobHandler>();
            builder.Services.AddSingleton<IPaymentRepository, PaymentRepository>();
            builder.Services.AddSingleton<IPremiumRepository, PremiumRepository>();
            builder.Services.AddSingleton<IJobHandler, PremiumReallocateJobHandler>();
            builder.Services.AddSingleton<IAdRepository, AdRepository>();
            builder.Services.AddSingleton<IJobHandler, AdPurgeJobHandler>();
            builder.Services.AddSingleton<IDigestRepository, DigestRepository>();
            builder.Services.AddSingleton<IJobHandler, AlertDigestJobHandler>();
            builder.Services.AddSingleton<DigestScheduler>();
            if (builder.Configuration.GetValue<bool?>("Jobs:SchedulerEnabled") ?? true) builder.Services.AddHostedService(sp => sp.GetRequiredService<DigestScheduler>());
            builder.Services.AddHostedService<SettingsRefresher>();

            // files, media processing and background jobs
            builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
            builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection("Media"));
            builder.Services.AddSingleton<DiskStorage>();
            builder.Services.AddSingleton<S3Storage>();
            builder.Services.AddSingleton<IStorageRouter, StorageRouter>();
            builder.Services.AddSingleton<IMediaProcessor, MediaProcessor>();
            builder.Services.AddSingleton<IFileRepository, FileRepository>();
            builder.Services.AddSingleton<MediaIngestService>();
            builder.Services.AddSingleton<IJobQueue, JobQueue>();
            builder.Services.AddSingleton<IJobHandler, MediaProcessJobHandler>();
            builder.Services.AddSingleton<JobRunner>();
            if (builder.Configuration.GetValue<bool?>("Jobs:Enabled") ?? true) builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());

            builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
            builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection("Google"));
            var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

            builder.Services.TryAddSingleton(TimeProvider.System);   // tests register their own clock first
            builder.Services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(auth.PasswordIterations));
            builder.Services.AddSingleton<DummyPasswordHash>();
            builder.Services.AddSingleton<ProtectedCookies>();
            builder.Services.AddSingleton<IGoogleIdTokenValidator, GoogleIdTokenValidator>();
            builder.Services.AddHttpClient("google", c => c.Timeout = TimeSpan.FromSeconds(10));

            var dp = builder.Services.AddDataProtection().SetApplicationName("Sayserne");
            if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
                dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));   // share this folder across instances

            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(AdminAuth.Scheme, o => AdminAuth.ConfigureCookie(o, builder.Environment.IsDevelopment()))
                .AddCookie(o =>
                {
                    o.Cookie.Name = "sayserne.auth";
                    o.Cookie.HttpOnly = true;
                    o.Cookie.SameSite = SameSiteMode.Lax;
                    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                    o.ExpireTimeSpan = TimeSpan.FromDays(30);   // upper bound only; each sign-in sets its own expiry from the Auth.SessionHours setting
                    o.SlidingExpiration = false;
                    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
                    // Re-check the account on every request: locking a user or changing a role takes effect immediately.
                    o.Events.OnValidatePrincipal = async c =>
                    {
                        var id = AuthSession.CurrentUserId(c.Principal!);
                        var users = c.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                        var user = id is null ? null : await users.GetByIdAsync(id.Value);
                        if (user is null || user.IsLocked || user.Role != c.Principal!.FindFirstValue(ClaimTypes.Role)
                            || AuthSession.PasswordFingerprint(user) != c.Principal!.FindFirstValue(AuthSession.PasswordClaim))
                        {
                            c.RejectPrincipal();
                            await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    };
                });
            // Back office: a separate cookie scheme; each route asks for one permission ("perm:<code>" policies are built on demand).
            builder.Services.AddAuthorizationBuilder()
                .AddPolicy(PermissionExtensions.StaffPolicy, p => p.AddAuthenticationSchemes(AdminAuth.Scheme).RequireAuthenticatedUser());
            builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
            builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

            builder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                // limits come from the admin-editable settings; the partition key carries the settings version so an edit applies to new windows
                o.AddPolicy("auth", ctx =>
                {
                    var settings = ctx.RequestServices.GetRequiredService<ISettingsService>();
                    return RateLimitPartition.GetFixedWindowLimiter($"{ctx.Connection.RemoteIpAddress}|{settings.Version}",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.GetInt("Auth.AuthRequestsPerMinute"), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
                });
                // writes (profile edits, bookings, chat messages, alerts): limited per signed-in user, falling back to the client IP
                o.AddPolicy(AdEndpoints.EventLimiter, ctx =>
                {
                    var settings = ctx.RequestServices.GetRequiredService<ISettingsService>();
                    return RateLimitPartition.GetFixedWindowLimiter($"ads|{ctx.Connection.RemoteIpAddress}|{settings.Version}",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.GetInt("Ads.EventsPerMinute"), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
                });
                o.AddPolicy(MarketplaceEndpoints.WriteLimiter, ctx =>
                {
                    var settings = ctx.RequestServices.GetRequiredService<ISettingsService>();
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"{AuthSession.CurrentUserId(ctx.User)?.ToString() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}|{settings.Version}",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.GetInt("Auth.WritesPerMinute"), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
                });
            });

            builder.Services.ConfigureHttpJsonOptions(o =>
                o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

            var app = builder.Build();
            app.Services.GetRequiredService<ISettingsService>().RefreshAsync().GetAwaiter().GetResult();   // fail fast if the database is not migrated

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
            app.MapAdminAuth();
            app.MapAdmin();
            app.MapAdminPlatform();
            app.MapAccountEmail();
            app.MapAds();
            app.MapPremium();
            app.MapPayments();
            app.MapMarketplace();
            app.MapPublicConfig();
            app.MapFiles();
            return app;
        }
    }
}
