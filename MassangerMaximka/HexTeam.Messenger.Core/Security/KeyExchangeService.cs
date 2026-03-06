using System.Security.Cryptography;

namespace HexTeam.Messenger.Core.Security;

/// <summary>
/// ECDH key exchange: each node generates an ephemeral EC key pair,
/// exchanges public keys during handshake, and derives a shared secret
/// for AES-GCM traffic encryption.
/// </summary>
public sealed class KeyExchangeService : IDisposable
{
    private readonly ECDiffieHellman _ecdh;

    public byte[] PublicKey { get; }

    public KeyExchangeService()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    public byte[] DeriveSharedSecret(byte[] peerPublicKey)
    {
        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKey, out _);

        var sharedSecret = _ecdh.DeriveKeyMaterial(peerEcdh.PublicKey);

        return DeriveAesKey(sharedSecret);
    }

    private static byte[] DeriveAesKey(byte[] sharedSecret)
    {
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            outputLength: 32,
            info: "HexTeam.Messenger.TrafficKey"u8.ToArray());
    }

    public void Dispose() => _ecdh.Dispose();
}
