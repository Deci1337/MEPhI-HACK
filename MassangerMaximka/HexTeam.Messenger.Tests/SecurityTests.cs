using HexTeam.Messenger.Core.Security;
using System.Security.Cryptography;
using System.Text;

namespace HexTeam.Messenger.Tests;

public class TrafficEncryptorTests
{
    [Fact]
    public void Encrypt_then_decrypt_returns_original()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encryptor = new TrafficEncryptor(key);
        var plaintext = Encoding.UTF8.GetBytes("secret message");

        var encrypted = encryptor.Encrypt(plaintext);
        var decrypted = encryptor.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_with_wrong_key_throws()
    {
        var enc = new TrafficEncryptor(RandomNumberGenerator.GetBytes(32));
        var dec = new TrafficEncryptor(RandomNumberGenerator.GetBytes(32));

        var encrypted = enc.Encrypt("hello"u8.ToArray());

        Assert.ThrowsAny<CryptographicException>(() => dec.Decrypt(encrypted));
    }

    [Fact]
    public void Encrypted_output_differs_from_plaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encryptor = new TrafficEncryptor(key);
        var plaintext = Encoding.UTF8.GetBytes("test data");

        var encrypted = encryptor.Encrypt(plaintext);

        Assert.NotEqual(plaintext, encrypted);
        Assert.True(encrypted.Length > plaintext.Length);
    }

    [Fact]
    public void Two_encryptions_produce_different_ciphertext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encryptor = new TrafficEncryptor(key);
        var plaintext = "same input"u8.ToArray();

        var a = encryptor.Encrypt(plaintext);
        var b = encryptor.Encrypt(plaintext);

        Assert.False(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void Short_key_throws()
    {
        Assert.Throws<ArgumentException>(() => new TrafficEncryptor(new byte[16]));
    }
}

public class KeyExchangeServiceTests
{
    [Fact]
    public void Both_sides_derive_same_shared_secret()
    {
        using var alice = new KeyExchangeService();
        using var bob = new KeyExchangeService();

        var aliceSecret = alice.DeriveSharedSecret(bob.PublicKey);
        var bobSecret = bob.DeriveSharedSecret(alice.PublicKey);

        Assert.Equal(aliceSecret, bobSecret);
        Assert.Equal(32, aliceSecret.Length);
    }

    [Fact]
    public void Different_peers_produce_different_secrets()
    {
        using var alice = new KeyExchangeService();
        using var bob = new KeyExchangeService();
        using var eve = new KeyExchangeService();

        var ab = alice.DeriveSharedSecret(bob.PublicKey);
        var ae = alice.DeriveSharedSecret(eve.PublicKey);

        Assert.False(ab.AsSpan().SequenceEqual(ae));
    }
}

public class E2EEncryptionServiceTests
{
    [Fact]
    public void Encrypt_decrypt_roundtrip()
    {
        using var alice = new E2EEncryptionService();
        using var bob = new E2EEncryptionService();

        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();

        alice.RegisterPeerPublicKey(bobId, bob.SessionPublicKey);
        bob.RegisterPeerPublicKey(aliceId, alice.SessionPublicKey);

        var plaintext = Encoding.UTF8.GetBytes("end-to-end encrypted message");
        var encrypted = alice.Encrypt(plaintext, bobId);
        var decrypted = bob.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_without_peer_key_throws()
    {
        using var alice = new E2EEncryptionService();
        var unknownPeer = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(
            () => alice.Encrypt("test"u8.ToArray(), unknownPeer));
    }

    [Fact]
    public void Third_party_cannot_decrypt()
    {
        using var alice = new E2EEncryptionService();
        using var bob = new E2EEncryptionService();
        using var eve = new E2EEncryptionService();

        var bobId = Guid.NewGuid();
        alice.RegisterPeerPublicKey(bobId, bob.SessionPublicKey);

        var encrypted = alice.Encrypt("secret"u8.ToArray(), bobId);

        Assert.ThrowsAny<CryptographicException>(() => eve.Decrypt(encrypted));
    }

    [Fact]
    public void HasPeerKey_returns_false_for_unknown()
    {
        using var svc = new E2EEncryptionService();
        Assert.False(svc.HasPeerKey(Guid.NewGuid()));
    }
}

public class PacketRateLimiterTests
{
    [Fact]
    public void Under_limit_is_allowed()
    {
        var limiter = new PacketRateLimiter(maxPacketsPerWindow: 5, windowSeconds: 10);
        var peer = Guid.NewGuid();

        for (int i = 0; i < 5; i++)
        {
            var result = limiter.Check(peer);
            Assert.True(result.Allowed);
        }
    }

    [Fact]
    public void Over_limit_is_blocked()
    {
        var limiter = new PacketRateLimiter(maxPacketsPerWindow: 3, windowSeconds: 60);
        var peer = Guid.NewGuid();

        limiter.Check(peer);
        limiter.Check(peer);
        limiter.Check(peer);
        var fourth = limiter.Check(peer);

        Assert.False(fourth.Allowed);
        Assert.Equal(1, fourth.TotalViolations);
    }

    [Fact]
    public void Different_peers_have_independent_limits()
    {
        var limiter = new PacketRateLimiter(maxPacketsPerWindow: 2, windowSeconds: 60);
        var peerA = Guid.NewGuid();
        var peerB = Guid.NewGuid();

        limiter.Check(peerA);
        limiter.Check(peerA);
        var a3 = limiter.Check(peerA);

        var b1 = limiter.Check(peerB);

        Assert.False(a3.Allowed);
        Assert.True(b1.Allowed);
    }

    [Fact]
    public void Violation_count_accumulates()
    {
        var limiter = new PacketRateLimiter(maxPacketsPerWindow: 1, windowSeconds: 60);
        var peer = Guid.NewGuid();

        limiter.Check(peer);
        limiter.Check(peer);
        var third = limiter.Check(peer);

        Assert.Equal(2, third.TotalViolations);
        Assert.Equal(2, limiter.GetViolationCount(peer));
    }

    [Fact]
    public void Reset_clears_peer_state()
    {
        var limiter = new PacketRateLimiter(maxPacketsPerWindow: 1, windowSeconds: 60);
        var peer = Guid.NewGuid();

        limiter.Check(peer);
        limiter.Check(peer);
        limiter.Reset(peer);

        var fresh = limiter.Check(peer);
        Assert.True(fresh.Allowed);
        Assert.Equal(0, fresh.TotalViolations);
    }

    [Fact]
    public void Blocks_peer_after_100_packets_in_10s()
    {
        var limiter = new PacketRateLimiter(maxPacketsPerWindow: 100, windowSeconds: 10);
        var peer = Guid.NewGuid();

        for (int i = 0; i < 100; i++)
            Assert.True(limiter.Check(peer).Allowed, $"Packet {i + 1} must be allowed");

        var overflow = limiter.Check(peer);
        Assert.False(overflow.Allowed);
        Assert.Equal(1, overflow.TotalViolations);
    }
}
