using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Unit.Services;

public class LoginAttemptTrackerTests
{
    private readonly LoginAttemptTracker _sut = new();

    [Fact]
    public void IsLockedOut_NoAttemptsYet_ReturnsFalse()
    {
        _sut.IsLockedOut("someone").Should().BeFalse();
    }

    [Fact]
    public void IsLockedOut_BelowThreshold_ReturnsFalse()
    {
        for (var i = 0; i < 4; i++) _sut.RecordFailure("someone");

        _sut.IsLockedOut("someone").Should().BeFalse();
    }

    [Fact]
    public void IsLockedOut_AtThreshold_ReturnsTrue()
    {
        for (var i = 0; i < 5; i++) _sut.RecordFailure("someone");

        _sut.IsLockedOut("someone").Should().BeTrue();
    }

    [Fact]
    public void RecordSuccess_ClearsFailureCount()
    {
        for (var i = 0; i < 5; i++) _sut.RecordFailure("someone");
        _sut.IsLockedOut("someone").Should().BeTrue();

        _sut.RecordSuccess("someone");

        _sut.IsLockedOut("someone").Should().BeFalse();
    }

    [Fact]
    public void IsLockedOut_IsPerUsername_DoesNotAffectOtherUsernames()
    {
        for (var i = 0; i < 5; i++) _sut.RecordFailure("attacker-target");

        _sut.IsLockedOut("attacker-target").Should().BeTrue();
        _sut.IsLockedOut("unrelated-user").Should().BeFalse();
    }
}
