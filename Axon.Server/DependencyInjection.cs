// ReSharper disable CheckNamespace

using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Axon.Core.Enums;
using Axon.Core.Helpers;
using Axon.Server.Hubs;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Axon.Server.DependencyInjection;

public static class DependencyInjection
{
    private const string AuthScheme = "AxonDashboard";
    private const string AuthPolicy = "AxonDashboardAuth";
    private const string WritePolicy = "AxonDashboardWrite";
    private const string LoginRateLimitPolicy = "AxonLogin";
    private const string ReadinessHealthCheckTag = "ready";

    // An instance is considered offline once its heartbeat (every 15s, see
    // AxonServerInstanceHeartbeat) is older than this - generous enough to absorb a couple of
    // missed/delayed heartbeats without flapping.
    private static readonly TimeSpan ServerInstanceOfflineTimeout = TimeSpan.FromSeconds(45);
    private static string? _dashboardHtml;
    private static string? _loginHtml;
    private static string? _jobHtml;
    private static byte[]? _faviconBytes;

    private static string GetEmbeddedResource(string suffix)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] GetEmbeddedResourceBytes(string suffix)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string GetDashboardHtml() => _dashboardHtml ??= GetEmbeddedResource("Dashboard.index.html");
    private static string GetLoginHtml() => _loginHtml ??= GetEmbeddedResource("Dashboard.login.html");
    private static string GetJobHtml() => _jobHtml ??= GetEmbeddedResource("Dashboard.job.html");
    private static byte[] GetFaviconBytes() => _faviconBytes ??= GetEmbeddedResourceBytes("Dashboard.axon.ico");

    // Falls back to "anonymous" rather than throwing when auth isn't configured (writes are open
    // in that mode) or the identity is otherwise unnamed, so audit logging never breaks a request.
    private static string GetAuditUsername(HttpContext http) => http.User.Identity?.Name ?? "anonymous";

    public static AxonServerBuilder AddAxonServer(this IServiceCollection services)
    {
        services.AddSignalR();
        services.TryAddSingleton<IAxonJobStore, InMemoryAxonJobStore>();
        services.TryAddSingleton<IAxonRecurringJobStore, InMemoryAxonRecurringJobStore>();
        services.TryAddSingleton<IAxonServerInstanceStore, InMemoryAxonServerInstanceStore>();
        services.TryAddSingleton<IDeviceConnectionRegistry, InMemoryDeviceConnectionRegistry>();
        services.AddSingleton<IAxonDashboardNotifier, AxonDashboardNotifier>();
        services.AddSingleton<IAxonJobService, AxonJobService>();
        services.AddScoped<IAxonRecurringJobService, AxonRecurringJobService>();
        services.AddHostedService<AxonJobProcessor>();
        services.AddHostedService<AxonRecurringJobProcessor>();
        services.AddHostedService<AxonServerInstanceHeartbeat>();
        services.AddHealthChecks().AddCheck<AxonJobStoreHealthCheck>("axon-job-store", tags: [ReadinessHealthCheckTag]);

        // On by default (1-day Succeeded-only retention) - chain .AddJobCleanup(...) to override
        // the retention/poll interval. TryAddSingleton here, plain AddSingleton in AddJobCleanup,
        // mirrors the same "explicit call registered after this one wins" pattern used for
        // IAxonJobStore etc. above.
        services.TryAddSingleton(new AxonJobCleanupOptions());
        services.AddHostedService<AxonJobCleanupProcessor>();

        return new AxonServerBuilder(services);
    }

    /// <summary>
    /// Maps the JSON API under /axon (job/recurring-job listing, retry, delete, trigger).
    /// Neither this nor the dashboard UI is mapped unless opted into explicitly.
    /// </summary>
    public static AxonServerBuilder AddAxonApiEndpoints(this AxonServerBuilder builder)
    {
        builder.Features.ApiEnabled = true;
        return builder;
    }

    /// <summary>
    /// Maps the JSON API plus the HTML dashboard UI (/axon/dashboard, /axon/login, job detail pages).
    /// </summary>
    public static AxonServerBuilder AddAxonDashboard(this AxonServerBuilder builder)
    {
        builder.AddAxonApiEndpoints();
        builder.Features.DashboardEnabled = true;
        return builder;
    }

    /// <summary>
    /// Overrides the default job-cleanup retention/poll interval. Cleanup itself is always on
    /// (see <c>AddAxonServer</c>) - by default it purges Succeeded jobs older than 1 day, sweeping
    /// once an hour. Failed and Skipped jobs are never purged automatically, regardless of what's
    /// configured here.
    /// </summary>
    public static AxonServerBuilder AddJobCleanup(this AxonServerBuilder builder, TimeSpan? retention = null, TimeSpan? pollInterval = null)
    {
        if (retention is { } r && r <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");

        // Plain AddSingleton here overrides AddAxonServer's TryAddSingleton default, since
        // AddJobCleanup is always called after AddAxonServer in the builder chain - the hosted
        // service itself is already registered once by AddAxonServer, not re-added here.
        builder.Services.AddSingleton(new AxonJobCleanupOptions
        {
            Retention = retention ?? TimeSpan.FromDays(1),
            PollInterval = pollInterval ?? TimeSpan.FromHours(1)
        });
        return builder;
    }

    /// <summary>
    /// Restricts this <c>Axon.Server</c> instance to only claim/dispatch jobs on the given
    /// queues, plus <c>"default"</c> which every instance serves implicitly (even one that never
    /// calls <c>AddQueues</c> at all) - so this always adds to what's served, never replaces the
    /// default with an exclusive allowlist. A job on a queue no live instance serves simply sits
    /// waiting (same as a job whose target device is offline) rather than failing - see
    /// docs/architecture.md#job-queues.
    /// </summary>
    public static AxonServerBuilder AddQueues(this AxonServerBuilder builder, params string[] queueNames)
    {
        if (queueNames.Length == 0 || queueNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty queue name must be provided.", nameof(queueNames));

        var servedQueues = new HashSet<string>(queueNames) { "default" };
        builder.Features.ServedQueues = servedQueues;
        return builder;
    }

    public static AxonServerBuilder AddAuthentication(this AxonServerBuilder builder, Action<DashboardAuthOptions> configureAuth)
    {
        var services = builder.Services;
        var authOptions = new DashboardAuthOptions();
        configureAuth(authOptions);
        if (authOptions.Users.Count == 0)
            throw new InvalidOperationException("At least one Axon dashboard user must be configured.");
        if (authOptions.Users.Any(u => string.IsNullOrEmpty(u.Username) || string.IsNullOrEmpty(u.Password)))
            throw new InvalidOperationException("Every Axon dashboard user must have a username and password.");
        if (authOptions.Users.Select(u => u.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != authOptions.Users.Count)
            throw new InvalidOperationException("Axon dashboard usernames must be unique.");
        services.AddSingleton(authOptions);
        services.AddSingleton(new DashboardUserCredentialStore(authOptions));

        // Keyed by IP: the request body (which carries the username) isn't available
        // synchronously when the rate limiter picks a partition, before the endpoint has run - so
        // IP is the only cheap partition key at this layer. This still bounds the same attacker
        // hammering many usernames from one IP; the login handler's separate per-username lockout
        // (below) bounds a distributed attempt against one specific username from many IPs.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitPolicy, http =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(5),
                        SegmentsPerWindow = 5,
                        QueueLimit = 0
                    }));
        });

        services.AddSingleton<LoginAttemptTracker>();

        // No default scheme is set here (AddAuthentication() takes no scheme argument): setting one
        // would overwrite AuthenticationOptions.DefaultScheme app-wide, silently breaking a host
        // app's own auth (e.g. JWT bearer) if it configures its scheme before or after this call.
        // Axon's cookie scheme is instead pinned explicitly wherever it's needed - see
        // AuthScheme and WritePolicy usages below and in UseAxonServer.
        services.AddAuthentication()
            .AddCookie(AuthScheme, options =>
            {
                options.Cookie.Name = "axon_dashboard_auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
                options.LoginPath = "/axon/login";
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = context.Request.Path.StartsWithSegments("/axon/dashboard")
                        ? StatusCodes.Status302Found
                        : StatusCodes.Status401Unauthorized;
                    if (context.Response.StatusCode == StatusCodes.Status302Found)
                        context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                // Signed in but lacking the required role (e.g. a ReadOnly user hitting a write
                // endpoint) is a 403, not a redirect to login - the cookie handler's default
                // AccessDenied behavior would otherwise send API callers through the login flow.
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization(options =>
        {
            // Pin the scheme explicitly on every Axon policy: with no app-wide default scheme set
            // above, authorization would otherwise try to authenticate against whatever scheme (if
            // any) the host app made default, which may not be Axon's cookie at all.
            options.AddPolicy(AuthPolicy, policy => policy
                .AddAuthenticationSchemes(AuthScheme)
                .RequireAuthenticatedUser());
            options.AddPolicy(WritePolicy, policy => policy
                .AddAuthenticationSchemes(AuthScheme)
                .RequireRole(nameof(DashboardRole.Admin)));
        });

        return builder;
    }

    public static void UseAxonServer(this WebApplication app)
    {
        // Only wire the auth middleware if .AddAuthentication(...) was actually chained: it's what
        // registers IAuthenticationSchemeProvider, and calling UseAuthentication() without it
        // throws at startup - which would otherwise make the documented "auth is opt-in" default
        // crash instead of just serving an open dashboard.
        var authConfigured = app.Services.GetService<DashboardAuthOptions>() is not null;
        if (authConfigured)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter(); // the /login endpoint's LoginRateLimitPolicy is only registered when auth is configured
        }

        app.MapHub<AxonHub>("/hubs/axon");

        // Always mapped, regardless of AddAxonApiEndpoints()/AddAxonDashboard() and regardless of
        // dashboard auth: orchestrator health probes need these independent of whether Axon's
        // HTTP API/dashboard is opted into, and typically can't authenticate anyway. Neither
        // exposes anything beyond up/down - /live never touches the job store, /ready only
        // reports the AxonJobStoreHealthCheck's Healthy/Unhealthy result.
        app.MapHealthChecks("/axon/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/axon/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadinessHealthCheckTag) }).AllowAnonymous();

        var features = app.Services.GetService<AxonServerFeatures>();
        var apiEnabled = features?.ApiEnabled ?? false;
        var dashboardEnabled = features?.DashboardEnabled ?? false;
        if (!apiEnabled && !dashboardEnabled)
        {
            app.Logger.LogInformation(
                "Axon API/dashboard are not mapped (call AddAxonServer().AddAxonApiEndpoints() for the JSON API, " +
                "or .AddAxonDashboard() for the API plus the HTML dashboard).");
            return;
        }

        var authEnabled = app.Services.GetService<DashboardAuthOptions>() is not null;
        if (!authEnabled)
        {
            app.Logger.LogWarning(
                "Axon dashboard has no authentication configured (chain .AddAuthentication(...) off AddAxonApiEndpoints()/AddAxonDashboard() to require sign-in). " +
                "Anyone who can reach this server can view and control jobs.");
        }

        var axon = app.MapGroup("/axon");

        if (dashboardEnabled)
        {
            if (authEnabled)
            {
                axon.MapGet("/", async (HttpContext http) =>
                {
                    var result = await http.AuthenticateAsync(AuthScheme);
                    return Results.Redirect(result.Succeeded ? "/axon/dashboard" : "/axon/login");
                }).AllowAnonymous();

                axon.MapGet("/login", () => Results.Content(GetLoginHtml(), "text/html")).AllowAnonymous();
            }
            else
            {
                axon.MapGet("/", () => Results.Redirect("/axon/dashboard")).AllowAnonymous();
            }

            axon.MapGet("/favicon.ico", () => Results.Bytes(GetFaviconBytes(), "image/x-icon")).AllowAnonymous();

            axon.MapGet("/dashboard", () => Results.Content(GetDashboardHtml(), "text/html"));

            axon.MapGet("/jobs/{jobId}/view", () => Results.Content(GetJobHtml(), "text/html"));

            var dashboardHub = app.MapHub<AxonDashboardHub>("/axon/hub");
            if (authEnabled)
            {
                dashboardHub.RequireAuthorization(AuthPolicy);
            }
        }

        if (authEnabled)
        {
            axon.MapPost("/login", async (
                HttpContext http, DashboardLoginRequest request, DashboardAuthOptions authOptions,
                DashboardUserCredentialStore credentials, LoginAttemptTracker attempts, ILogger<AxonAuditLog> auditLogger) =>
            {
                var remoteIp = http.Connection.RemoteIpAddress?.ToString();

                if (!string.IsNullOrEmpty(request.Username) && attempts.IsLockedOut(request.Username))
                {
                    await Task.Delay(300);
                    AxonAuditLog.LoginFailed(auditLogger, request.Username, remoteIp);
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }

                var user = string.IsNullOrEmpty(request.Username)
                    ? null
                    : authOptions.Users.FirstOrDefault(u => string.Equals(u.Username, request.Username, StringComparison.OrdinalIgnoreCase));

                if (user is null || string.IsNullOrEmpty(request.Password) || !credentials.Verify(request.Username, request.Password))
                {
                    // Constant-ish delay so failed attempts don't respond meaningfully faster than successful ones.
                    await Task.Delay(300);
                    if (!string.IsNullOrEmpty(request.Username)) attempts.RecordFailure(request.Username);
                    AxonAuditLog.LoginFailed(auditLogger, request.Username, remoteIp);
                    return Results.Unauthorized();
                }

                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role.ToString())
                };
                var identity = new ClaimsIdentity(claims, AuthScheme);
                await http.SignInAsync(AuthScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
                });

                attempts.RecordSuccess(user.Username);
                AxonAuditLog.LoginSucceeded(auditLogger, user.Username, remoteIp);
                return Results.NoContent();
            }).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);

            axon.MapPost("/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(AuthScheme);
                return Results.NoContent();
            });

            axon.MapGet("/me", async (HttpContext http) =>
            {
                var result = await http.AuthenticateAsync(AuthScheme);
                return Results.Ok(new
                {
                    username = result.Principal?.Identity?.Name,
                    role = result.Principal?.FindFirstValue(ClaimTypes.Role)
                });
            }).AllowAnonymous();
        }
        else
        {
            axon.MapGet("/me", () => Results.Ok(new { username = (string?)null, role = (string?)null })).AllowAnonymous();
        }

        if (apiEnabled)
        {
            axon.MapGet("/jobs", async (IAxonJobStore jobStore, int skip = 0, int take = 0, JobState? state = null) =>
            {
                var states = state is null ? null : new[] { state.Value };
                var jobs = await jobStore.GetJobs(skip, take == 0 ? 20 : take, states);
                return Results.Ok(jobs);
            });

            axon.MapGet("/jobs/{jobId}", async (IAxonJobStore jobStore, string jobId) =>
            {
                var job = await jobStore.GetJob(jobId);
                return job is null ? Results.NotFound() : Results.Ok(job);
            });

            var deleteJob = axon.MapDelete("/jobs/{jobId}", async (HttpContext http, IAxonJobStore jobStore, IAxonDashboardNotifier notifier, ILogger<AxonAuditLog> auditLogger, string jobId) =>
            {
                await jobStore.DeleteJob(jobId);
                await notifier.JobsChanged();
                AxonAuditLog.JobDeleted(auditLogger, GetAuditUsername(http), jobId);
                return Results.NoContent();
            });

            var retryJob = axon.MapPost("/jobs/{jobId}/retry", async (HttpContext http, IAxonJobStore jobStore, IAxonDashboardNotifier notifier, ILogger<AxonAuditLog> auditLogger, string jobId) =>
            {
                var job = await jobStore.GetJob(jobId);
                if (job is null) return Results.NotFound();

                await jobStore.Requeue(jobId);
                await notifier.JobsChanged();
                AxonAuditLog.JobRetried(auditLogger, GetAuditUsername(http), jobId);
                return Results.NoContent();
            });

            axon.MapGet("/jobs/{jobId}/history", async (IAxonJobStore jobStore, string jobId) =>
                Results.Ok(await jobStore.GetHistory(jobId)));

            // Lifetime counts, not a live-row COUNT(*): Succeeded/Failed/Skipped include jobs
            // already cleaned up by the retention sweep, so these numbers never drop when cleanup
            // runs. See IAxonJobStore.CountJobsByState.
            axon.MapGet("/stats", async (IAxonJobStore jobStore) =>
            {
                var counts = await jobStore.CountJobsByState();
                return Results.Ok(new
                {
                    Total = counts.Values.Sum(),
                    Enqueued = counts.GetValueOrDefault(JobState.Enqueued),
                    Scheduled = counts.GetValueOrDefault(JobState.Scheduled),
                    Processing = counts.GetValueOrDefault(JobState.Processing),
                    Succeeded = counts.GetValueOrDefault(JobState.Succeeded),
                    Failed = counts.GetValueOrDefault(JobState.Failed),
                });
            });

            axon.MapGet("/recurring-jobs", async (IAxonRecurringJobStore recurringJobStore, int skip = 0, int take = 0) =>
                Results.Ok(await recurringJobStore.GetAll(skip, take == 0 ? 20 : take)));

            axon.MapGet("/servers", async (IAxonServerInstanceStore instanceStore) =>
            {
                var now = DateTime.UtcNow.Ticks;
                var instances = await instanceStore.GetAll();
                return Results.Ok(instances
                    .OrderByDescending(i => i.LastSeenAt)
                    .Select(i => new
                    {
                        i.InstanceId,
                        i.MachineName,
                        i.StartedAt,
                        i.LastSeenAt,
                        i.ServedQueues,
                        IsOnline = now - i.LastSeenAt <= ServerInstanceOfflineTimeout.Ticks
                    }));
            });

            axon.MapGet("/clients", async (IDeviceConnectionRegistry deviceRegistry, IAxonJobStore jobStore) =>
            {
                var clients = await deviceRegistry.GetAll();
                var processingDevices = (await jobStore.GetJobs(take: int.MaxValue, states: [JobState.Processing]))
                    .Select(j => j.DeviceName)
                    .ToHashSet();

                return Results.Ok(clients.Select(c => new
                {
                    c.DeviceName,
                    c.ConnectionId,
                    c.ConnectedAt,
                    IsProcessing = processingDevices.Contains(c.DeviceName)
                }));
            });

            var deleteRecurring = axon.MapDelete("/recurring-jobs/{recurringJobId}", async (HttpContext http, IAxonRecurringJobStore recurringJobStore, IAxonDashboardNotifier notifier, ILogger<AxonAuditLog> auditLogger, string recurringJobId) =>
            {
                await recurringJobStore.Remove(recurringJobId);
                await notifier.RecurringJobsChanged();
                AxonAuditLog.RecurringJobDeleted(auditLogger, GetAuditUsername(http), recurringJobId);
                return Results.NoContent();
            });

            var triggerRecurring = axon.MapPost("/recurring-jobs/{recurringJobId}/trigger", async (
                HttpContext http, IAxonRecurringJobStore recurringJobStore, IAxonJobStore jobStore, IAxonDashboardNotifier notifier, ILogger<AxonAuditLog> auditLogger, string recurringJobId) =>
            {
                var recurringJob = await recurringJobStore.GetById(recurringJobId);
                if (recurringJob is null) return Results.NotFound();

                var jobId = Guid.NewGuid().ToString();
                await jobStore.AddJob(new Job(recurringJob)
                {
                    JobId = jobId,
                    DeviceName = recurringJob.DeviceName,
                    State = JobState.Enqueued
                });
                await notifier.JobsChanged();
                AxonAuditLog.RecurringJobTriggered(auditLogger, GetAuditUsername(http), recurringJobId, jobId);

                return Results.Ok(new { jobId });
            });

            var pauseRecurring = axon.MapPost("/recurring-jobs/{recurringJobId}/pause", async (
                HttpContext http, IAxonRecurringJobStore recurringJobStore, IAxonDashboardNotifier notifier, ILogger<AxonAuditLog> auditLogger, string recurringJobId) =>
            {
                if (await recurringJobStore.GetById(recurringJobId) is null) return Results.NotFound();

                await recurringJobStore.SetPaused(recurringJobId, isPaused: true);
                await notifier.RecurringJobsChanged();
                AxonAuditLog.RecurringJobPaused(auditLogger, GetAuditUsername(http), recurringJobId);
                return Results.NoContent();
            });

            var resumeRecurring = axon.MapPost("/recurring-jobs/{recurringJobId}/resume", async (
                HttpContext http, IAxonRecurringJobStore recurringJobStore, IAxonDashboardNotifier notifier, ILogger<AxonAuditLog> auditLogger, string recurringJobId) =>
            {
                if (await recurringJobStore.GetById(recurringJobId) is null) return Results.NotFound();

                await recurringJobStore.SetPaused(recurringJobId, isPaused: false);
                await notifier.RecurringJobsChanged();
                AxonAuditLog.RecurringJobResumed(auditLogger, GetAuditUsername(http), recurringJobId);
                return Results.NoContent();
            });

            var skipNextRecurring = axon.MapPost("/recurring-jobs/{recurringJobId}/skip-next", async (
                HttpContext http, IAxonRecurringJobStore recurringJobStore, IAxonDashboardNotifier notifier, ILogger<AxonAuditLog> auditLogger, string recurringJobId) =>
            {
                var recurringJob = await recurringJobStore.GetById(recurringJobId);
                if (recurringJob is null) return Results.NotFound();

                var cron = CronExpression.Parse(recurringJob.CronExpression);
                var newNextRunAt = cron.GetNextOccurrence(new DateTimeOffset(recurringJob.NextRunAt, TimeSpan.Zero)).UtcTicks;
                await recurringJobStore.SkipNext(recurringJobId, newNextRunAt);
                await notifier.RecurringJobsChanged();
                AxonAuditLog.RecurringJobNextSkipped(auditLogger, GetAuditUsername(http), recurringJobId);
                return Results.NoContent();
            });

            if (authEnabled)
            {
                deleteJob.RequireAuthorization(WritePolicy);
                retryJob.RequireAuthorization(WritePolicy);
                deleteRecurring.RequireAuthorization(WritePolicy);
                triggerRecurring.RequireAuthorization(WritePolicy);
                pauseRecurring.RequireAuthorization(WritePolicy);
                resumeRecurring.RequireAuthorization(WritePolicy);
                skipNextRecurring.RequireAuthorization(WritePolicy);
            }
        }

        if (authEnabled)
        {
            axon.RequireAuthorization(AuthPolicy);
        }
        else
        {
            axon.AllowAnonymous();
        }
    }
}

public class DashboardLoginRequest
{
    public string Username { get; set; } = null!;
    public string Password { get; set; } = null!;
}

public class AxonServerFeatures
{
    public bool ApiEnabled { get; set; }
    public bool DashboardEnabled { get; set; }

    /// <summary>
    /// Queues this instance claims/dispatches jobs from - see <c>AxonServerBuilder.AddQueues</c>.
    /// Defaults to just <c>"default"</c> for an instance that never calls <c>AddQueues</c>.
    /// </summary>
    public IReadOnlySet<string> ServedQueues { get; set; } = new HashSet<string> { "default" };
}

public class AxonServerBuilder
{
    public IServiceCollection Services { get; }
    public AxonServerFeatures Features { get; } = new();

    public AxonServerBuilder(IServiceCollection services)
    {
        Services = services;
        services.AddSingleton(Features);
    }
}
