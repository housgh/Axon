using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Unit.Services;

public class DashboardUserCredentialStoreTests
{
    private static DashboardUserCredentialStore CreateSut() => new(new DashboardAuthOptions
    {
        Users =
        [
            new DashboardUser { Username = "admin", Password = "P@ssw0rd", Role = DashboardRole.Admin },
            new DashboardUser { Username = "viewer", Password = "letmein", Role = DashboardRole.ReadOnly }
        ]
    });

    [Fact]
    public void Verify_CorrectUsernameAndPassword_ReturnsTrue()
    {
        var sut = CreateSut();

        sut.Verify("admin", "P@ssw0rd").Should().BeTrue();
    }

    [Fact]
    public void Verify_UsernameIsCaseInsensitive()
    {
        var sut = CreateSut();

        sut.Verify("ADMIN", "P@ssw0rd").Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var sut = CreateSut();

        sut.Verify("admin", "wrong").Should().BeFalse();
    }

    [Fact]
    public void Verify_UnknownUsername_ReturnsFalse()
    {
        var sut = CreateSut();

        sut.Verify("nobody", "anything").Should().BeFalse();
    }

    [Fact]
    public void Verify_PasswordFromOneUser_DoesNotMatchAnotherUser()
    {
        var sut = CreateSut();

        sut.Verify("viewer", "P@ssw0rd").Should().BeFalse();
    }
}
