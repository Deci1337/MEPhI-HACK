using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace HexTeam.Messenger.Core.Services;

public sealed class HandshakeVerifier
{
    private readonly Guid _localNodeId;
    private readonly string _localFingerprint;
    private readonly byte[] _challengeSecret;

    public HandshakeVerifier(NodeIdentity localIdentity)
    {
        _localNodeId = localIdentity.NodeId;
        _localFingerprint = localIdentity.Fingerprint;
        _challengeSecret = RandomNumberGenerator.GetBytes(32);
    }

    public HelloPacket CreateHello(int listenPort) => new()
    {
        NodeId = _localNodeId,
        DisplayName = string.Empty,
        Fingerprint = _localFingerprint,
        ListenPort = listenPort,
        ProtocolVersion = "1.0"
    };

    public HandshakeResult VerifyHello(HelloPacket hello, Guid claimedNodeId)
    {
        if (hello.NodeId != claimedNodeId)
            return HandshakeResult.NodeIdMismatch;

        var expected = ComputeFingerprint(hello.NodeId);
        if (!string.Equals(hello.Fingerprint, expected, StringComparison.OrdinalIgnoreCase))
            return HandshakeResult.FingerprintMismatch;

        if (hello.NodeId == _localNodeId)
            return HandshakeResult.SelfConnection;

        if (string.IsNullOrEmpty(hello.ProtocolVersion) || hello.ProtocolVersion != "1.0")
            return HandshakeResult.VersionMismatch;

        return HandshakeResult.Accepted;
    }

    public bool ValidateOrigin(Envelope envelope)
    {
        if (envelope.OriginNodeId == Guid.Empty)
            return false;

        if (envelope.OriginNodeId == _localNodeId && envelope.HopCount == 0)
            return false;

        return true;
    }

    private static string ComputeFingerprint(Guid nodeId)
    {
        var bytes = nodeId.ToByteArray();
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..8];
    }
}

public enum HandshakeResult : byte
{
    Accepted = 0,
    NodeIdMismatch = 1,
    FingerprintMismatch = 2,
    SelfConnection = 3,
    VersionMismatch = 4
}
