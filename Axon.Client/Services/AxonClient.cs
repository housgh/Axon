using System.Linq.Expressions;
using Axon.Core.Helpers;
using Axon.Core.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Axon.Client.Services;

public interface IAxonClient
{
    Task<string> EnqueueAsync(Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null);
    Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null);
    Task<string> ScheduleAsync(TimeSpan delay, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null);
    Task<string> ScheduleAsync<TType>(TimeSpan delay, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null);
    Task<string> ScheduleAsync(DateTimeOffset scheduledFor, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null);
    Task<string> ScheduleAsync<TType>(DateTimeOffset scheduledFor, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null);
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
        hubConnection.StartAsync();
    }

    private void InitClient()
    {
        _hubConnection.On("Invoke", (string jobId, JobInfo jobInfo) =>
            OnInvoke(jobId, jobInfo));

        _hubConnection.Reconnected += _ => _hubConnection.InvokeAsync("Register", _deviceName);
    }

    private readonly HubConnection _hubConnection;
    private readonly string _deviceName;

    public Task<string> EnqueueAsync(Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy), null);

    public Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy), null);

    public Task<string> ScheduleAsync(TimeSpan delay, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy), DateTimeOffset.UtcNow.Add(delay));

    public Task<string> ScheduleAsync<TType>(TimeSpan delay, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy), DateTimeOffset.UtcNow.Add(delay));

    public Task<string> ScheduleAsync(DateTimeOffset scheduledFor, Expression<Action> methodCall, AxonRetryPolicy? retryPolicy = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy), scheduledFor);

    public Task<string> ScheduleAsync<TType>(DateTimeOffset scheduledFor, Expression<Action<TType>> methodCall, AxonRetryPolicy? retryPolicy = null) =>
        EnqueueInternalAsync(GetJobInfo(methodCall, retryPolicy), scheduledFor);

    public Task AddOrUpdateRecurringAsync(string recurringJobId, string cronExpression, Expression<Action> methodCall) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, GetJobInfo(methodCall, null));

    public Task AddOrUpdateRecurringAsync<TType>(string recurringJobId, string cronExpression, Expression<Action<TType>> methodCall) =>
        AddOrUpdateRecurringInternalAsync(recurringJobId, cronExpression, GetJobInfo(methodCall, null));

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

    private static JobInfo? GetJobInfo(LambdaExpression methodCall, AxonRetryPolicy? retryPolicy)
    {
        if (methodCall.Body is not MethodCallExpression call) return null;
        return new JobInfo
        {
            Arguments = GetArguments(call),
            Assembly = call.Method.DeclaringType!.Assembly.FullName!,
            MethodName = call.Method.Name,
            DeclaringType = call.Method.DeclaringType!.FullName!,
            RetryPolicy = retryPolicy,
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
