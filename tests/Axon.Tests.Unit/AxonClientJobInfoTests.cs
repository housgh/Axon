using System.Linq.Expressions;
using Axon.Client.Services;
using Axon.Core;
using FluentAssertions;

namespace Axon.Tests.Unit;

public class AxonClientJobInfoTests
{
    private class TestJobs
    {
        public void PlainMethod() { }

        [AxonConcurrencyLimit("email-sender", 5)]
        public void LimitedMethod() { }
    }

    [Fact]
    public void GetJobInfo_NoExplicitKeyNoAttribute_LeavesConcurrencyFieldsNull()
    {
        Expression<Action<TestJobs>> call = x => x.PlainMethod();

        var jobInfo = AxonClient.GetJobInfo(call, retryPolicy: null, concurrencyKey: null, maxConcurrent: null);

        jobInfo!.ConcurrencyKey.Should().BeNull();
        jobInfo.MaxConcurrent.Should().BeNull();
    }

    [Fact]
    public void GetJobInfo_ExplicitKeyNoAttribute_UsesExplicitKey()
    {
        Expression<Action<TestJobs>> call = x => x.PlainMethod();

        var jobInfo = AxonClient.GetJobInfo(call, retryPolicy: null, concurrencyKey: "explicit-key", maxConcurrent: 3);

        jobInfo!.ConcurrencyKey.Should().Be("explicit-key");
        jobInfo.MaxConcurrent.Should().Be(3);
    }

    [Fact]
    public void GetJobInfo_NoExplicitKeyWithAttribute_UsesAttributeValues()
    {
        Expression<Action<TestJobs>> call = x => x.LimitedMethod();

        var jobInfo = AxonClient.GetJobInfo(call, retryPolicy: null, concurrencyKey: null, maxConcurrent: null);

        jobInfo!.ConcurrencyKey.Should().Be("email-sender");
        jobInfo.MaxConcurrent.Should().Be(5);
    }

    [Fact]
    public void GetJobInfo_ExplicitKeyWithAttribute_ExplicitKeyWins()
    {
        Expression<Action<TestJobs>> call = x => x.LimitedMethod();

        var jobInfo = AxonClient.GetJobInfo(call, retryPolicy: null, concurrencyKey: "override-key", maxConcurrent: 99);

        jobInfo!.ConcurrencyKey.Should().Be("override-key");
        jobInfo.MaxConcurrent.Should().Be(99);
    }
}
