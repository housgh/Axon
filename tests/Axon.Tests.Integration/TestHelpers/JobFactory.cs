using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Services;

namespace Axon.Tests.Integration.TestHelpers;

internal static class JobFactory
{
    public static Job CreateJob(
        string? jobId = null,
        string deviceName = "device-1",
        JobState state = JobState.Enqueued,
        int attempts = 0,
        int maxAttempts = 3,
        long? scheduledFor = null,
        AxonRetryPolicy? retryPolicy = null,
        string? concurrencyKey = null,
        int? maxConcurrent = null,
        string? parentJobId = null,
        bool continueOnParentFailure = false) =>
        new(new JobInfo
        {
            MethodName = "DoWork",
            Assembly = "Axon.Tests",
            DeclaringType = "Axon.Tests.SomeType",
            Arguments = ["hello", 42],
            RetryPolicy = retryPolicy,
            ConcurrencyKey = concurrencyKey,
            MaxConcurrent = maxConcurrent
        })
        {
            JobId = jobId ?? Guid.NewGuid().ToString(),
            DeviceName = deviceName,
            State = state,
            Attempts = attempts,
            MaxAttempts = maxAttempts,
            ScheduledFor = scheduledFor,
            ParentJobId = parentJobId,
            ContinueOnParentFailure = continueOnParentFailure
        };
}
