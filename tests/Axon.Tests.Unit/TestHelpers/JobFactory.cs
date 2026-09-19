using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Services;

namespace Axon.Tests.Unit.TestHelpers;

internal static class JobFactory
{
    public static Job CreateJob(
        string? jobId = null,
        string deviceName = "device-1",
        JobState state = JobState.Enqueued,
        int attempts = 0,
        int maxAttempts = 3,
        long? scheduledFor = null,
        long? processingDeadline = null,
        long enqueuedAt = 0) =>
        new(new JobInfo
        {
            MethodName = "DoWork",
            Assembly = "Axon.Tests",
            DeclaringType = "Axon.Tests.SomeType",
            Arguments = []
        })
        {
            JobId = jobId ?? Guid.NewGuid().ToString(),
            DeviceName = deviceName,
            State = state,
            Attempts = attempts,
            MaxAttempts = maxAttempts,
            ScheduledFor = scheduledFor,
            ProcessingDeadline = processingDeadline,
            EnqueuedAt = enqueuedAt
        };
}
