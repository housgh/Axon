using System.Linq.Expressions;
using System.Text.Json;
using Axon.Client.Services;
using Axon.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Axon.Server.Services;

/// <summary>
/// The <see cref="IAxonClient"/> registered by <c>AddInProcessClient</c> (monolith mode): writes
/// jobs straight to <see cref="IAxonJobService"/>/<see cref="IAxonRecurringJobService"/> in this
/// process instead of sending them over a SignalR connection to a remote Axon.Server.
/// </summary>
internal sealed class InProcessAxonClient(
    IAxonJobService jobService,
    IServiceScopeFactory scopeFactory,
    AxonInProcessClientOptions options) : IAxonClient
{
    // Matches SignalR's JSON hub protocol defaults (camelCase), so a job's arguments reach the
    // executor in exactly the shape they would have arriving over the wire in microservice mode.
    private static readonly JsonSerializerOptions ArgumentSerializerOptions = new(JsonSerializerDefaults.Web);

    public Task<string> EnqueueAsync(Expression<Action> methodCall, AxonEnqueueOptions? enqueueOptions = null) =>
        EnqueueInternalAsync(JobInfoFactory.Create(methodCall, enqueueOptions), null);

    public Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall, AxonEnqueueOptions? enqueueOptions = null) =>
        EnqueueInternalAsync(JobInfoFactory.Create(methodCall, enqueueOptions), null);

    public Task<string> ScheduleAsync(TimeSpan delay, Expression<Action> methodCall, AxonEnqueueOptions? enqueueOptions = null) =>
        EnqueueInternalAsync(JobInfoFactory.Create(methodCall, enqueueOptions), DateTimeOffset.UtcNow.Add(delay));

    public Task<string> ScheduleAsync<TType>(TimeSpan delay, Expression<Action<TType>> methodCall, AxonEnqueueOptions? enqueueOptions = null) =>
        EnqueueInternalAsync(JobInfoFactory.Create(methodCall, enqueueOptions), DateTimeOffset.UtcNow.Add(delay));

    public Task<string> ScheduleAsync(DateTimeOffset scheduledFor, Expression<Action> methodCall, AxonEnqueueOptions? enqueueOptions = null) =>
        EnqueueInternalAsync(JobInfoFactory.Create(methodCall, enqueueOptions), scheduledFor);

    public Task<string> ScheduleAsync<TType>(DateTimeOffset scheduledFor, Expression<Action<TType>> methodCall, AxonEnqueueOptions? enqueueOptions = null) =>
        EnqueueInternalAsync(JobInfoFactory.Create(methodCall, enqueueOptions), scheduledFor);

    public Task<string> ContinueWithAsync(string parentJobId, Expression<Action> methodCall, bool continueOnParentFailure = false, AxonEnqueueOptions? enqueueOptions = null) =>
        ContinueWithInternalAsync(parentJobId, JobInfoFactory.Create(methodCall, enqueueOptions), continueOnParentFailure);

    public Task<string> ContinueWithAsync<TType>(string parentJobId, Expression<Action<TType>> methodCall, bool continueOnParentFailure = false, AxonEnqueueOptions? enqueueOptions = null) =>
        ContinueWithInternalAsync(parentJobId, JobInfoFactory.Create(methodCall, enqueueOptions), continueOnParentFailure);

    public Task AddOrUpdateRecurringAsync(string recurringJobId, string cronExpression, Expression<Action> methodCall, CancellationToken cancellationToken = default) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, JobInfoFactory.Create(methodCall, null));

    public Task AddOrUpdateRecurringAsync<TType>(string recurringJobId, string cronExpression, Expression<Action<TType>> methodCall, CancellationToken cancellationToken = default) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, JobInfoFactory.Create(methodCall, null));

    public async Task RemoveRecurringAsync(string recurringJobId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAxonRecurringJobService>().RemoveAsync(recurringJobId);
    }

    private async Task<string> EnqueueInternalAsync(JobInfo? jobInfo, DateTimeOffset? scheduledFor)
    {
        var jobId = Guid.NewGuid().ToString();
        await jobService.EnqueueAsync(options.DeviceName, jobId, Normalize(jobInfo), scheduledFor?.UtcTicks);
        return jobId;
    }

    private async Task<string> ContinueWithInternalAsync(string parentJobId, JobInfo? jobInfo, bool continueOnParentFailure)
    {
        var jobId = Guid.NewGuid().ToString();
        await jobService.EnqueueContinuationAsync(options.DeviceName, jobId, Normalize(jobInfo), parentJobId, continueOnParentFailure);
        return jobId;
    }

    private async Task AddOrUpdateRecurringInternalAsync(string recurringJobId, string cronExpression, JobInfo? jobInfo)
    {
        var normalized = Normalize(jobInfo);
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAxonRecurringJobService>()
            .AddOrUpdateAsync(options.DeviceName, recurringJobId, normalized, cronExpression);
    }

    // AxonJobExecutor (via JsonElementHelper) expects every argument as a JsonElement, which is
    // what SignalR deserialization and every durable store hand back. Arguments built locally are
    // still live CLR objects, which the in-memory store would keep as-is - so serialize them here,
    // once, to make in-process jobs behave identically regardless of which store is configured.
    private static JobInfo Normalize(JobInfo? jobInfo)
    {
        if (jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");

        jobInfo.Arguments = jobInfo.Arguments
            .Select(arg => (object?)JsonSerializer.SerializeToElement(arg, ArgumentSerializerOptions))
            .ToList();
        return jobInfo;
    }
}
