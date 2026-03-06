namespace HexTeam.Messenger.Core.Models;

public sealed class NodeIdentity
{
    public Guid NodeId { get; }
    public string DisplayName { get; }
    public string Fingerprint { get; }

    public NodeIdentity(Guid nodeId, string displayName)
    {
        NodeId = nodeId;
        DisplayName = displayName;
        Fingerprint = ComputeFingerprint(nodeId);
    }

    public static NodeIdentity Generate(string displayName)
        => new(Guid.NewGuid(), displayName);

    private static string ComputeFingerprint(Guid nodeId)
    {
        var bytes = nodeId.ToByteArray();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash)[..8];
    }

    public override string ToString() => $"{DisplayName}#{Fingerprint}";
}
