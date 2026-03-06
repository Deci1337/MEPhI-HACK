using System.Security.Cryptography;

namespace HexTeam.Messenger.Core.Security;

/// <summary>
/// End-to-end encryption for chat messages.
/// Each session uses an ephemeral ECDH key pair.
/// Message payload is encrypted with AES-256-GCM so relay nodes
/// can forward the envelope but cannot read the plaintext.
/// </summary>
public sealed class E2EEncryptionService : IDisposable
{
    private readonly ECDiffieHellman _sessionKey;
    private readonly Dictionary<Guid, byte[]> _peerKeys = new();

    public byte[] SessionPublicKey { get; }

    public E2EEncryptionService()
    {
        _sessionKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        SessionPublicKey = _sessionKey.PublicKey.ExportSubjectPublicKeyInfo();
    }

    public void RegisterPeerPublicKey(Guid peerNodeId, byte[] publicKey)
    {
        _peerKeys[peerNodeId] = publicKey;
    }

    public bool HasPeerKey(Guid peerNodeId) => _peerKeys.ContainsKey(peerNodeId);

    public EncryptedPayload Encrypt(byte[] plaintext, Guid recipientNodeId)
    {
        if (!_peerKeys.TryGetValue(recipientNodeId, out var peerPub))
            throw new InvalidOperationException($"No public key registered for peer {recipientNodeId}");

        var sessionSecret = DeriveSessionKey(peerPub);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(sessionSecret, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedPayload
        {
            Nonce = nonce,
            Tag = tag,
            Ciphertext = ciphertext,
            SenderPublicKey = SessionPublicKey
        };
    }

    public byte[] Decrypt(EncryptedPayload payload)
    {
        var sessionSecret = DeriveSessionKey(payload.SenderPublicKey);
        var plaintext = new byte[payload.Ciphertext.Length];

        using var aes = new AesGcm(sessionSecret, 16);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);
        return plaintext;
    }

    private byte[] DeriveSessionKey(byte[] peerPublicKey)
    {
        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        var raw = _sessionKey.DeriveKeyMaterial(peerEcdh.PublicKey);

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            raw,
            outputLength: 32,
            info: "HexTeam.Messenger.E2E"u8.ToArray());
    }

    public void Dispose() => _sessionKey.Dispose();
}

public sealed class EncryptedPayload
{
    public byte[] Nonce { get; init; } = [];
    public byte[] Tag { get; init; } = [];
    public byte[] Ciphertext { get; init; } = [];
    public byte[] SenderPublicKey { get; init; } = [];
}
