using System.Linq.Expressions;
using Axon.Core;
using Axon.Core.Enums;
using Axon.Core.Helpers;
using Axon.Core.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Axon.Client.Services;

public interface IAxonClient
{
    /// <summary>
    /// Enqueues <paramref name="methodCall"/> to run as soon as a connected device with a
    /// matching device name picks it up (see <see cref="ScheduleAsync(TimeSpan,Expression{Action},AxonEnqueueOptions?)"/>
    /// to instead delay dispatch). Returns the new job's id once the server has durably stored
    /// it - awaiting this does not wait for the job to actually run.
    /// </summary>
    Task<string> EnqueueAsync(Expression<Action> methodCall, AxonEnqueueOptions? options = null);
    /// <inheritdoc cref="EnqueueAsync(Expression{Action},AxonEnqueueOptions?)"/>
    Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall, AxonEnqueueOptions? options = null);
    /// <summary>
    /// Like <see cref="EnqueueAsync(Expression{Action},AxonEnqueueOptions?)"/>, but the job only
    /// becomes eligible for dispatch after <paramref name="delay"/> has elapsed.
    /// </summary>
    Task<string> ScheduleAsync(TimeSpan delay, Expression<Action> methodCall, AxonEnqueueOptions? options = null);
    /// <inheritdoc cref="ScheduleAsync(TimeSpan,Expression{Action},AxonEnqueueOptions?)"/>
    Task<string> ScheduleAsync<TType>(TimeSpan delay, Expression<Action<TType>> methodCall, AxonEnqueueOptions? options = null);
    /// <summary>
    /// Like <see cref="EnqueueAsync(Expression{Action},AxonEnqueueOptions?)"/>, but the job only
    /// becomes eligible for dispatch at <paramref name="scheduledFor"/>.
    /// </summary>
    Task<string> ScheduleAsync(DateTimeOffset scheduledFor, Expression<Action> methodCall, AxonEnqueueOptions? options = null);
    /// <inheritdoc cref="ScheduleAsync(DateTimeOffset,Expression{Action},AxonEnqueueOptions?)"/>
    Task<string> ScheduleAsync<TType>(DateTimeOffset scheduledFor, Expression<Action<TType>> methodCall, AxonEnqueueOptions? options = null);

    /// <summary>
    /// Enqueues a job that only runs after <paramref name="parentJobId"/> reaches a terminal
    /// state: promoted on the parent's success, or on the parent's failure only if
    /// <paramref name="continueOnParentFailure"/> is true (default false - the continuation is
    /// left permanently unscheduled if the parent fails).
    /// </summary>
    Task<string> ContinueWithAsync(string parentJobId, Expression<Action> methodCall, bool continueOnParentFailure = false, AxonEnqueueOptions? options = null);
    Task<string> ContinueWithAsync<TType>(string parentJobId, Expression<Action<TType>> methodCall, bool continueOnParentFailure = false, AxonEnqueueOptions? options = null);

    Task AddOrUpdateRecurringAsync(string recurringJobId, string cronExpression, Expression<Action> methodCall, CancellationToken cancellationToken = default);
    Task AddOrUpdateRecurringAsync<TType>(string recurringJobId, string cronExpression, Expression<Action<TType>> methodCall, CancellationToken cancellationToken = default);
    Task RemoveRecurringAsync(string recurringJobId, CancellationToken cancellationToken = default);
}

internal class AxonClient : IAxonClient
{
    public AxonClient(HubConnection hubConnection)
    {
        _hubConnection = hubConnection;
        _deviceName = $"{Environment.MachineName}_{Guid.NewGuid()}";
        InitClient();
        StartAndRegister();
    }

    private void InitClient()
    {
        _hubConnection.On("Invoke", (string jobId, JobInfo jobInfo) =>
            OnInvoke(jobId, jobInfo));

        _hubConnection.Reconnected += _ => _hubConnection.InvokeAsync("Register", _deviceName);
    }

    // Registers on the initial connect too, not just Reconnected, so the device shows up in the
    // dashboard's Clients tab as soon as it comes online instead of only after its first job.
    // WithAutomaticReconnect() only covers a connection that already succeeded once - the first
    // StartAsync() here gets no retry from SignalR, so a transient failure (e.g. the server or a
    // load balancer in front of it isn't accepting connections yet) is retried here instead of
    // being left to crash the process as an unhandled exception from this async void method.
    private async void StartAndRegister()
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await _hubConnection.StartAsync();
                await _hubConnection.InvokeAsync("Register", _deviceName);
                return;
            }
            catch
            {
                await Task.Delay(delay);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private readonly HubConnection _hubConnection;
    private readonly string _deviceName;

    public Task<string> EnqueueAsync(Expression<Action> methodCall, AxonEnqueueOptions? options = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, options), null, options?.CancellationToken ?? default);

    public Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall, AxonEnqueueOptions? options = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, options), null, options?.CancellationToken ?? default);

    public Task<string> ScheduleAsync(TimeSpan delay, Expression<Action> methodCall, AxonEnqueueOptions? options = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, options), DateTimeOffset.UtcNow.Add(delay), options?.CancellationToken ?? default);

    public Task<string> ScheduleAsync<TType>(TimeSpan delay, Expression<Action<TType>> methodCall, AxonEnqueueOptions? options = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, options), DateTimeOffset.UtcNow.Add(delay), options?.CancellationToken ?? default);

    public Task<string> ScheduleAsync(DateTimeOffset scheduledFor, Expression<Action> methodCall, AxonEnqueueOptions? options = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, options), scheduledFor, options?.CancellationToken ?? default);

    public Task<string> ScheduleAsync<TType>(DateTimeOffset scheduledFor, Expression<Action<TType>> methodCall, AxonEnqueueOptions? options = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, options), scheduledFor, options?.CancellationToken ?? default);

    public Task<string> ContinueWithAsync(string parentJobId, Expression<Action> methodCall, bool continueOnParentFailure = false, AxonEnqueueOptions? options = null) =>
        ContinueWithInternalAsync(parentJobId, GetJobInfo(methodCall, options), continueOnParentFailure, options?.CancellationToken ?? default);

    public Task<string> ContinueWithAsync<TType>(string parentJobId, Expression<Action<TType>> methodCall, bool continueOnParentFailure = false, AxonEnqueueOptions? options = null) =>
        ContinueWithInternalAsync(parentJobId, GetJobInfo(methodCall, options), continueOnParentFailure, options?.CancellationToken ?? default);

    public Task AddOrUpdateRecurringAsync(string recurringJobId, string cronExpression, Expression<Action> methodCall, CancellationToken cancellationToken = default) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, GetJobInfo(methodCall, null), cancellationToken);

    public Task AddOrUpdateRecurringAsync<TType>(string recurringJobId, string cronExpression, Expression<Action<TType>> methodCall, CancellationToken cancellationToken = default) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, GetJobInfo(methodCall, null), cancellationToken);

    public Task RemoveRecurringAsync(string recurringJobId, CancellationToken cancellationToken = default) =>
        _hubConnection.InvokeCoreAsync("RemoveRecurring", [recurringJobId], cancellationToken);

    private Task AddOrUpdateRecurringInternalAsync(string recurringJobId, string cronExpression, JobInfo? jobInfo, CancellationToken cancellationToken)
    {
        if (jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");

        return _hubConnection.InvokeCoreAsync("AddOrUpdateRecurring", [_deviceName, recurringJobId, jobInfo, cronExpression], cancellationToken);
    }

    private async Task<string> EnqueueInternalAsync(JobInfo? jobInfo, DateTimeOffset? scheduledFor, CancellationToken cancellationToken)
    {
        if (jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");

        var jobId = Guid.NewGuid().ToString();
        await _hubConnection.InvokeCoreAsync("Enqueue", [_deviceName, jobId, jobInfo, scheduledFor?.UtcTicks], cancellationToken);

        return jobId;
    }

    private async Task<string> ContinueWithInternalAsync(string parentJobId, JobInfo? jobInfo, bool continueOnParentFailure, CancellationToken cancellationToken)
    {
        if (jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");

        var jobId = Guid.NewGuid().ToString();
        await _hubConnection.InvokeCoreAsync("EnqueueContinuation", [_deviceName, jobId, jobInfo, parentJobId, continueOnParentFailure], cancellationToken);

        return jobId;
    }

    // internal (not private) so Axon.Tests.Unit can verify the concurrency key/attribute
    // precedence logic directly, without needing a real or faked HubConnection.
    internal static JobInfo? GetJobInfo(LambdaExpression methodCall, AxonEnqueueOptions? options)
    {
        if (methodCall.Body is not MethodCallExpression call) return null;

        var concurrencyKey = options?.ConcurrencyKey;
        var maxConcurrent = options?.MaxConcurrent;

        // An explicit concurrencyKey argument always wins over the attribute; the attribute is
        // only consulted when the caller didn't pass one, so a call-site override never has to
        // fight a method-level default.
        if (concurrencyKey is null)
        {
            var limit = call.Method.GetCustomAttributes(typeof(AxonConcurrencyLimitAttribute), inherit: false)
                .Cast<AxonConcurrencyLimitAttribute>()
                .FirstOrDefault();
            if (limit is not null)
            {
                concurrencyKey = limit.ConcurrencyKey;
                maxConcurrent = limit.MaxConcurrent;
            }
        }

        return new JobInfo
        {
            Arguments = GetArguments(call),
            Assembly = call.Method.DeclaringType!.Assembly.FullName!,
            MethodName = call.Method.Name,
            DeclaringType = call.Method.DeclaringType!.FullName!,
            RetryPolicy = options?.RetryPolicy,
            ConcurrencyKey = concurrencyKey,
            MaxConcurrent = maxConcurrent,
            Priority = options?.Priority ?? JobPriority.Medium,
            QueueName = options?.QueueName ?? "default",
        };
    }

    private static List<object?> GetArguments(MethodCallExpression call)
    {
        return call.Arguments
            .Select(arg => Expression.Lambda(arg).Compile().DynamicInvoke()).ToList();
    }

    private async void OnInvoke(string jobId, JobInfo jobInfo)
    {
        ActivatedJob activated = default;
        try
        {
            activated = JobActivator.Current.CreateInstance(jobInfo);
            var methodInfo = activated.Instance?.GetType().GetMethod(jobInfo.MethodName);
            if (methodInfo is null)
            {
                await _hubConnection.InvokeAsync("OnFail", jobId, $"Could not find method: {jobInfo.MethodName}");
                return;
            }

            var arguments = JsonElementHelper.ToObjectArray(jobInfo.Arguments.ToArray());
            methodInfo.Invoke(activated.Instance, arguments);
            await _hubConnection.InvokeAsync("OnSuccess", jobId);
        }
        catch (FileNotFoundException)
        {
            await _hubConnection.InvokeAsync("OnFail", jobId, $"Could not load assembly: {jobInfo.Assembly}");
        }
        catch (Exception e)
        {
            await _hubConnection.InvokeAsync("OnFail", jobId, e.ToString());
        }
        finally
        {
            // Disposed only now - after the job method has actually run, not right after
            // activation - so a scoped dependency (e.g. ServiceProviderJobActivator's DI scope)
            // injected into the job's constructor stays alive for the whole call.
            activated.Scope?.Dispose();
        }
    }
}
