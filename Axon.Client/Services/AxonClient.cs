using System.Linq.Expressions;
using Axon.Core;
using Axon.Core.Helpers;
using Axon.Core.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Axon.Client.Services;

public interface IAxonClient
{
    /// <summary>
    /// Enqueues <paramref name="methodCall"/> to run as soon as a connected device with a
    /// matching device name picks it up (see <see cref="ScheduleAsync(TimeSpan,Expression{Action},AxonRetryPolicy?,string?,int?)"/>
    /// to instead delay dispatch). Returns the new job's id once the server has durably stored
    /// it - awaiting this does not wait for the job to actually run.
    /// <para/>
    /// <paramref name="retryPolicy"/> overrides Axon.Server's default retry/backoff schedule for
    /// this job only; <paramref name="concurrencyKey"/> plus <paramref name="maxConcurrent"/>
    /// cap how many jobs sharing that key may be Processing at once across the whole fleet
    /// (enforced atomically by the job store, not just locally).
    /// </summary>
    Task<string> EnqueueAsync(Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null);
    /// <inheritdoc cref="EnqueueAsync(Expression{Action},AxonRetryPolicy?,string?,int?)"/>
    Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null);
    /// <summary>
    /// Like <see cref="EnqueueAsync(Expression{Action},AxonRetryPolicy?,string?,int?)"/>, but the
    /// job only becomes eligible for dispatch after <paramref name="delay"/> has elapsed.
    /// </summary>
    Task<string> ScheduleAsync(TimeSpan delay, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null);
    /// <inheritdoc cref="ScheduleAsync(TimeSpan,Expression{Action},AxonRetryPolicy?,string?,int?)"/>
    Task<string> ScheduleAsync<TType>(TimeSpan delay, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null);
    /// <summary>
    /// Like <see cref="EnqueueAsync(Expression{Action},AxonRetryPolicy?,string?,int?)"/>, but the
    /// job only becomes eligible for dispatch at <paramref name="scheduledFor"/>.
    /// </summary>
    Task<string> ScheduleAsync(DateTimeOffset scheduledFor, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null);
    /// <inheritdoc cref="ScheduleAsync(DateTimeOffset,Expression{Action},AxonRetryPolicy?,string?,int?)"/>
    Task<string> ScheduleAsync<TType>(DateTimeOffset scheduledFor, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null);

    /// <summary>
    /// Enqueues a job that only runs after <paramref name="parentJobId"/> reaches a terminal
    /// state: promoted on the parent's success, or on the parent's failure only if
    /// <paramref name="continueOnParentFailure"/> is true (default false - the continuation is
    /// left permanently unscheduled if the parent fails).
    /// </summary>
    Task<string> ContinueWithAsync(string parentJobId, Expression<Action> methodCall, bool continueOnParentFailure = false, AxonRetryPolicy? retryPolicy = null);
    Task<string> ContinueWithAsync<TType>(string parentJobId, Expression<Action<TType>> methodCall, bool continueOnParentFailure = false, AxonRetryPolicy? retryPolicy = null);

    Task AddOrUpdateRecurringAsync(string recurringJobId, string cronExpression, Expression<Action> methodCall);
    Task AddOrUpdateRecurringAsync<TType>(string recurringJobId, string cronExpression, Expression<Action<TType>> methodCall);
    Task RemoveRecurringAsync(string recurringJobId);
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

    public Task<string> EnqueueAsync(Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy, concurrencyKey, maxConcurrent), null);

    public Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy, concurrencyKey, maxConcurrent), null);

    public Task<string> ScheduleAsync(TimeSpan delay, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy, concurrencyKey, maxConcurrent), DateTimeOffset.UtcNow.Add(delay));

    public Task<string> ScheduleAsync<TType>(TimeSpan delay, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy, concurrencyKey, maxConcurrent), DateTimeOffset.UtcNow.Add(delay));

    public Task<string> ScheduleAsync(DateTimeOffset scheduledFor, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy, concurrencyKey, maxConcurrent), scheduledFor);

    public Task<string> ScheduleAsync<TType>(DateTimeOffset scheduledFor, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null, string? concurrencyKey = null, int? maxConcurrent = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy, concurrencyKey, maxConcurrent), scheduledFor);

    public Task<string> ContinueWithAsync(string parentJobId, Expression<Action> methodCall, bool continueOnParentFailure = false, AxonRetryPolicy? retryPolicy = null) =>
        ContinueWithInternalAsync(parentJobId, GetJobInfo(methodCall, retryPolicy, null, null), continueOnParentFailure);

    public Task<string> ContinueWithAsync<TType>(string parentJobId, Expression<Action<TType>> methodCall, bool continueOnParentFailure = false, AxonRetryPolicy? retryPolicy = null) =>
        ContinueWithInternalAsync(parentJobId, GetJobInfo(methodCall, retryPolicy, null, null), continueOnParentFailure);

    public Task AddOrUpdateRecurringAsync(string recurringJobId, string cronExpression, Expression<Action> methodCall) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, GetJobInfo(methodCall, null, null, null));

    public Task AddOrUpdateRecurringAsync<TType>(string recurringJobId, string cronExpression, Expression<Action<TType>> methodCall) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, GetJobInfo(methodCall, null, null, null));

    public Task RemoveRecurringAsync(string recurringJobId) =>
        _hubConnection.InvokeAsync("RemoveRecurring", recurringJobId);

    private Task AddOrUpdateRecurringInternalAsync(string recurringJobId, string cronExpression, JobInfo? jobInfo)
    {
        if (jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");

        return _hubConnection.InvokeAsync("AddOrUpdateRecurring", _deviceName, recurringJobId, jobInfo, cronExpression);
    }

    private async Task<string> EnqueueInternalAsync(JobInfo? jobInfo, DateTimeOffset? scheduledFor)
    {
        if (jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");

        var jobId = Guid.NewGuid().ToString();
        await _hubConnection.InvokeAsync("Enqueue", _deviceName, jobId, jobInfo, scheduledFor?.UtcTicks);

        return jobId;
    }

    private async Task<string> ContinueWithInternalAsync(string parentJobId, JobInfo? jobInfo, bool continueOnParentFailure)
    {
        if (jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");

        var jobId = Guid.NewGuid().ToString();
        await _hubConnection.InvokeAsync("EnqueueContinuation", _deviceName, jobId, jobInfo, parentJobId, continueOnParentFailure);

        return jobId;
    }

    // internal (not private) so Axon.Tests.Unit can verify the concurrency key/attribute
    // precedence logic directly, without needing a real or faked HubConnection.
    internal static JobInfo? GetJobInfo(LambdaExpression methodCall, AxonRetryPolicy? retryPolicy, string? concurrencyKey, int? maxConcurrent)
    {
        if (methodCall.Body is not MethodCallExpression call) return null;

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
            RetryPolicy = retryPolicy,
            ConcurrencyKey = concurrencyKey,
            MaxConcurrent = maxConcurrent,
        };
    }

    private static List<object?> GetArguments(MethodCallExpression call)
    {
        return call.Arguments
            .Select(arg => Expression.Lambda(arg).Compile().DynamicInvoke()).ToList();
    }

    private async void OnInvoke(string jobId, JobInfo jobInfo)
    {
        try
        {
            var instance = JobActivator.Current.CreateInstance(jobInfo);
            var methodInfo = instance?.GetType().GetMethod(jobInfo.MethodName);
            if (methodInfo is null)
            {
                await _hubConnection.InvokeAsync("OnFail", jobId, $"Could not find method: {jobInfo.MethodName}");
                return;
            }

            var arguments = JsonElementHelper.ToObjectArray(jobInfo.Arguments.ToArray());
            methodInfo.Invoke(instance, arguments);
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
    }
}
