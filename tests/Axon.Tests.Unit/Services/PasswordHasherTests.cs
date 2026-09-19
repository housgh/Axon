using Axon.Server.Services;
using FluentAssertions;

namespace Axon.Tests.Unit.Services;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_ThenVerify_WithCorrectPassword_ReturnsTrue()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        PasswordHasher.Verify("correct horse battery staple", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        PasswordHasher.Verify("wrong password", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        // Different random salts per call - proves the salt isn't fixed/reused, which is what
        // makes precomputed rainbow-table attacks against the stored hashes infeasible.
        var hash1 = PasswordHasher.Hash("same-password");
        var hash2 = PasswordHasher.Hash("same-password");

        hash1.Should().NotBe(hash2);
        PasswordHasher.Verify("same-password", hash1).Should().BeTrue();
        PasswordHasher.Verify("same-password", hash2).Should().BeTrue();
    }

    [Fact]
    public void Verify_MalformedStoredHash_ReturnsFalseRatherThanThrowing()
    {
        PasswordHasher.Verify("anything", "not-a-valid-hash-format").Should().BeFalse();
        PasswordHasher.Verify("anything", "").Should().BeFalse();
        PasswordHasher.Verify("anything", "onlyonepart").Should().BeFalse();
    }
}
