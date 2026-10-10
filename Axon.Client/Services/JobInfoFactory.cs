using System.Linq.Expressions;
using Axon.Core;
using Axon.Core.Enums;
using Axon.Core.Models;

namespace Axon.Client.Services;

/// <summary>
/// Turns an enqueue call's method-call expression into the <see cref="JobInfo"/> Axon.Server
/// stores and later dispatches. Shared by every <see cref="IAxonClient"/> implementation (the
/// SignalR-backed client and Axon.Server's in-process one) so both build identical jobs.
/// </summary>
public static class JobInfoFactory
{
    /// <summary>
    /// Returns null if <paramref name="methodCall"/>'s body isn't a method call.
    /// </summary>
    public static JobInfo? Create(LambdaExpression methodCall, AxonEnqueueOptions? options)
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
}
