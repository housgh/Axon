// ReSharper disable CheckNamespace

using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Axon.Core.Enums;
using Axon.Server.Hubs;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axon.Server.DependencyInjection;

public static class DependencyInjection
{
    private const string AuthScheme = "AxonDashboard";
    private static string? _dashboardHtml;
    private static string? _loginHtml;
    private static string? _jobHtml;

    private static string GetEmbeddedResource(string suffix)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetDashboardHtml() => _dashboardHtml ??= GetEmbeddedResource("Dashboard.index.html");
    private static string GetLoginHtml() => _loginHtml ??= GetEmbeddedResource("Dashboard.login.html");
    private static string GetJobHtml() => _jobHtml ??= GetEmbeddedResource("Dashboard.job.html");

    private static bool PasswordsMatch(string a, string b)
    {
        // Compare fixed-size hashes in constant time so neither a length mismatch
        // nor a byte mismatch can be inferred from response timing.
        var aHash = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var bHash = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }

    public static void AddAxonServer(this IServiceCollection services, Action<DashboardAuthOptions> configureAuth)
    {
        var authOptions = new DashboardAuthOptions();
        configureAuth(authOptions);
        if (string.IsNullOrEmpty(authOptions.Password))
            throw new InvalidOperationException("Axon dashboard password must be configured.");
        services.AddSingleton(authOptions);

        services.AddSignalR();
        services.TryAddSingleton<IAxonJobStore, InMemoryAxonJobStore>();
        services.TryAddSingleton<IAxonRecurringJobStore, InMemoryAxonRecurringJobStore>();
        services.AddSingleton<IDeviceConnectionRegistry, DeviceConnectionRegistry>();
        services.AddScoped<IAxonJobService, AxonJobService>();
        services.AddScoped<IAxonRecurringJobService, AxonRecurringJobService>();
        services.AddHostedService<AxonJobProcessor>();
        services.AddHostedService<AxonRecurringJobProcessor>();

        services.AddAuthentication(AuthScheme)
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
            });
        services.AddAuthorization();
    }

    public static void UseAxonServer(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHub<AxonHub>("/hubs/axon");

        var axon = app.MapGroup("/axon");

        axon.MapGet("/", (HttpContext http) => Results.Redirect(
            http.User.Identity?.IsAuthenticated == true ? "/axon/dashboard" : "/axon/login")).AllowAnonymous();

        axon.MapGet("/login", () => Results.Content(GetLoginHtml(), "text/html")).AllowAnonymous();

        axon.MapPost("/login", async (HttpContext http, DashboardLoginRequest request, DashboardAuthOptions authOptions) =>
        {
            if (string.IsNullOrEmpty(request.Password) || !PasswordsMatch(request.Password, authOptions.Password))
            {
                // Constant-ish delay so failed attempts don't respond meaningfully faster than successful ones.
                await Task.Delay(300);
                return Results.Unauthorized();
            }

            var claims = new[] { new Claim(ClaimTypes.Name, "axon-admin") };
            var identity = new ClaimsIdentity(claims, AuthScheme);
            await http.SignInAsync(AuthScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });

            return Results.NoContent();
        }).AllowAnonymous();

        axon.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthScheme);
            return Results.NoContent();
        });

        axon.MapGet("/jobs", async (IAxonJobStore jobStore, int skip, int take, JobState? state) =>
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

        axon.MapDelete("/jobs/{jobId}", async (IAxonJobStore jobStore, string jobId) =>
        {
            await jobStore.DeleteJob(jobId);
            return Results.NoContent();
        });

        axon.MapPost("/jobs/{jobId}/retry", async (IAxonJobStore jobStore, string jobId) =>
        {
            var job = await jobStore.GetJob(jobId);
            if (job is null) return Results.NotFound();

            await jobStore.Requeue(jobId);
            return Results.NoContent();
        });

        axon.MapGet("/jobs/{jobId}/history", async (IAxonJobStore jobStore, string jobId) =>
            Results.Ok(await jobStore.GetHistory(jobId)));

        axon.MapGet("/recurring-jobs", async (IAxonRecurringJobStore recurringJobStore) =>
            Results.Ok(await recurringJobStore.GetAll()));

        axon.MapDelete("/recurring-jobs/{recurringJobId}", async (IAxonRecurringJobStore recurringJobStore, string recurringJobId) =>
        {
            await recurringJobStore.Remove(recurringJobId);
            return Results.NoContent();
        });

        axon.MapPost("/recurring-jobs/{recurringJobId}/trigger", async (
            IAxonRecurringJobStore recurringJobStore, IAxonJobStore jobStore, string recurringJobId) =>
        {
            var recurringJob = (await recurringJobStore.GetAll())
                .FirstOrDefault(r => r.RecurringJobId == recurringJobId);
            if (recurringJob is null) return Results.NotFound();

            var jobId = Guid.NewGuid().ToString();
            await jobStore.AddJob(new Job(recurringJob)
            {
                JobId = jobId,
                DeviceName = recurringJob.DeviceName,
                State = JobState.Enqueued
            });

            return Results.Ok(new { jobId });
        });

        axon.MapGet("/dashboard", () => Results.Content(GetDashboardHtml(), "text/html"));

        axon.MapGet("/jobs/{jobId}/view", () => Results.Content(GetJobHtml(), "text/html"));

        axon.RequireAuthorization();
    }
}

public class DashboardLoginRequest
{
    public string Password { get; set; } = null!;
}
