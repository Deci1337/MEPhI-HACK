using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;

namespace HexTeam.Messenger.Tests;

public class HandshakeVerifierTests
{
    private static readonly NodeIdentity LocalNode = NodeIdentity.Generate("TestNode");
    private static readonly NodeIdentity RemoteNode = NodeIdentity.Generate("RemoteNode");

    [Fact]
    public void Valid_hello_returns_accepted()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var hello = new HelloPacket
        {
            NodeId = RemoteNode.NodeId,
            Fingerprint = RemoteNode.Fingerprint,
            ListenPort = 45680,
            ProtocolVersion = "1.0"
        };

        var result = verifier.VerifyHello(hello, RemoteNode.NodeId);
        Assert.Equal(HandshakeResult.Accepted, result);
    }

    [Fact]
    public void NodeId_mismatch_is_rejected()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var hello = new HelloPacket
        {
            NodeId = RemoteNode.NodeId,
            Fingerprint = RemoteNode.Fingerprint,
            ListenPort = 45680,
            ProtocolVersion = "1.0"
        };

        var result = verifier.VerifyHello(hello, Guid.NewGuid());
        Assert.Equal(HandshakeResult.NodeIdMismatch, result);
    }

    [Fact]
    public void Spoofed_fingerprint_is_rejected()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var hello = new HelloPacket
        {
            NodeId = RemoteNode.NodeId,
            Fingerprint = "DEADBEEF",
            ListenPort = 45680,
            ProtocolVersion = "1.0"
        };

        var result = verifier.VerifyHello(hello, RemoteNode.NodeId);
        Assert.Equal(HandshakeResult.FingerprintMismatch, result);
    }

    [Fact]
    public void Self_connection_is_rejected()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var hello = new HelloPacket
        {
            NodeId = LocalNode.NodeId,
            Fingerprint = LocalNode.Fingerprint,
            ListenPort = 45680,
            ProtocolVersion = "1.0"
        };

        var result = verifier.VerifyHello(hello, LocalNode.NodeId);
        Assert.Equal(HandshakeResult.SelfConnection, result);
    }

    [Fact]
    public void Wrong_protocol_version_is_rejected()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var hello = new HelloPacket
        {
            NodeId = RemoteNode.NodeId,
            Fingerprint = RemoteNode.Fingerprint,
            ListenPort = 45680,
            ProtocolVersion = "2.0"
        };

        var result = verifier.VerifyHello(hello, RemoteNode.NodeId);
        Assert.Equal(HandshakeResult.VersionMismatch, result);
    }

    [Fact]
    public void ValidateOrigin_rejects_empty_origin()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var envelope = new Envelope
        {
            OriginNodeId = Guid.Empty,
            PacketType = PacketType.ChatEnvelope
        };

        Assert.False(verifier.ValidateOrigin(envelope));
    }

    [Fact]
    public void ValidateOrigin_rejects_self_originated_zero_hop()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var envelope = new Envelope
        {
            OriginNodeId = LocalNode.NodeId,
            HopCount = 0,
            PacketType = PacketType.ChatEnvelope
        };

        Assert.False(verifier.ValidateOrigin(envelope));
    }

    [Fact]
    public void ValidateOrigin_accepts_valid_remote_packet()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var envelope = new Envelope
        {
            OriginNodeId = RemoteNode.NodeId,
            HopCount = 1,
            PacketType = PacketType.ChatEnvelope
        };

        Assert.True(verifier.ValidateOrigin(envelope));
    }

    [Fact]
    public void CreateHello_uses_local_fingerprint()
    {
        var verifier = new HandshakeVerifier(LocalNode);
        var hello = verifier.CreateHello(45680);

        Assert.Equal(LocalNode.NodeId, hello.NodeId);
        Assert.Equal(LocalNode.Fingerprint, hello.Fingerprint);
    }
}
