using System.Linq.Expressions;
using System.Reflection;
using Axon.Core.Helpers;
using Axon.Core.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Axon.Client.Services;

public interface IAxonClient
{
    Task<string> EnqueueAsync(Expression<Action> methodCall);
    Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall);
    
}

internal class AxonClient : IAxonClient
{
    public AxonClient(HubConnection hubConnection)
    {
        _hubConnection = hubConnection;
        hubConnection.StartAsync();
        _deviceName = $"{Environment.MachineName}_{Guid.NewGuid()}";
        InitClient();
    }

    private void InitClient()
    {
        _hubConnection.On("Invoke", (string jobId, JobInfo jobInfo) => 
            OnInvoke(jobId, jobInfo));
    }

    private readonly HubConnection _hubConnection;
    private readonly string _deviceName;

    public async Task<string> EnqueueAsync(Expression<Action> methodCall)
    {
        var jobId = Guid.NewGuid().ToString();
        var jobInfo = GetJobInfo(methodCall);
        if(jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");
        await _hubConnection.InvokeAsync("Enqueue", _deviceName, jobId, jobInfo);
        
        return jobId;
    }

    public async Task<string> EnqueueAsync<TType>(Expression<Action<TType>> methodCall)
    {
        var jobId = Guid.NewGuid().ToString();
        var jobInfo = GetJobInfo(methodCall);
        if(jobInfo is null)
            throw new InvalidOperationException("Invalid method provided.");
        await _hubConnection.InvokeAsync("Enqueue", _deviceName, jobId, jobInfo);
        
        return jobId;
    }

    private static JobInfo? GetJobInfo(Expression<Action> methodCall)
    {
        if (methodCall.Body is not MethodCallExpression call) return null;
        return new JobInfo
        {
            Arguments = GetArguments(call),
            Assembly = call.Method.DeclaringType!.Assembly.FullName!,
            MethodName = call.Method.Name,
            DeclaringType = call.Method.DeclaringType!.FullName!,
        };
    }
    
    private static JobInfo? GetJobInfo<TType>(Expression<Action<TType>> methodCall)
    {
        if (methodCall.Body is not MethodCallExpression call) return null;
        return new JobInfo
        {
            Arguments = GetArguments(call),
            Assembly = call.Method.DeclaringType!.Assembly.FullName!,
            MethodName = call.Method.Name,
            DeclaringType = call.Method.DeclaringType!.FullName!,
        };
    }

    private static List<object?> GetArguments(MethodCallExpression call)
    {
        return call.Arguments
            .Select(arg => Expression.Lambda(arg).Compile().DynamicInvoke()).ToList();
    }

    private void OnInvoke(string jobId, JobInfo jobInfo)
    {
        try
        {
            var instance = JobActivator.Current.CreateInstance(jobInfo);
            var methodInfo = instance?.GetType().GetMethod(jobInfo.MethodName);
            if (methodInfo is null)
            {
                return;
            }

            var arguments = JsonElementHelper.ToObjectArray(jobInfo.Arguments.ToArray());
            methodInfo.Invoke(instance, arguments);
        }
        catch (FileNotFoundException)
        {
            _hubConnection.InvokeAsync("OnFail", jobId, $"Could not assembly: {jobInfo.Assembly}");
        }
        catch (Exception e)
        {
            
        }
    }
}