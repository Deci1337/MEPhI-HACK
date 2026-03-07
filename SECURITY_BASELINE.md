# Security Baseline

This document describes the trust model and security assumptions for the HexTeam Messenger demo.
It is intended as a concise reference for the team and for presentation to judges.

## Trust Model

### Who Is Trusted

- **Local node**: always trusted. Identified by `NodeIdentity` (a stable `NodeId: Guid` and a derived fingerprint).
- **Direct peer after successful handshake**: trusted for message delivery in the current session.
  Handshake acceptance requires `HandshakeResult.Accepted` from `HandshakeVerifier.VerifyHello()`.
- **Relay nodes**: trusted to forward envelopes faithfully, but NOT trusted with plaintext content
  when E2E encryption is active.

### What Is Verified at Each Step

| Check | Where | Mechanism |
|---|---|---|
| Node identity integrity | `HandshakeVerifier.VerifyHello` | `NodeId` matches claimed ID, fingerprint is SHA-256 of `NodeId` |
| Self-connection prevention | `HandshakeVerifier.VerifyHello` | `SelfConnection` result if `NodeId == localNodeId` |
| Protocol version match | `HandshakeVerifier.VerifyHello` | Exact match on `ProtocolVersion = "1.0"` |
| Packet origin plausibility | `PacketRouter.ValidateEnvelope` via `HandshakeVerifier.ValidateOrigin` | `OriginNodeId != Guid.Empty`, blocks self-originated packets at hop 0 |
| Replay protection | `ISeenPacketStore` | Per-packet `PacketId` tracked with TTL (300 s by default) |
| Loop / storm protection | `RelayService` + `HopCount >= MaxHops` | Packets dropped when hop limit reached; never relayed back to sender |
| Payload integrity per hop | AES-256-GCM tag (when E2E active) | `E2EEncryptionService` — any tampered ciphertext will fail GCM authentication |

### E2E Encryption (Available but Optional)

`E2EEncryptionService` implements ephemeral ECDH (NIST P-256) + AES-256-GCM.
Each session creates a fresh key pair; the session public key is exchanged during connection setup.
Relay nodes forward the encrypted envelope and cannot read the plaintext.

Current integration status: the service exists and is tested independently.
Full integration into `PacketRouter` (encrypting `ChatPacket` payloads before send) is a
straightforward next step but was not completed to preserve time for core reliability features.

## Assumptions for This Demo

1. **Local network is physically trusted** — the demo runs in a closed LAN / hotspot. No NAT traversal,
   no internet exposure.
2. **No certificate authority** — fingerprints are derived deterministically from `NodeId` (SHA-256).
   This is sufficient to detect accidental mismatches but does NOT protect against a deliberate
   node ID spoofing attack if the attacker can observe and replicate fingerprint derivation.
3. **Relay nodes are honest** — relay nodes do not tamper with `HopCount` or drop selective packets.
   With E2E encryption, confidentiality is protected even from relay nodes. Without it, relay nodes
   can read plaintext.
4. **No persistent key storage** — session keys are ephemeral and lost on restart. History is stored
   in plaintext in memory.
5. **Replay window is 300 seconds** — packets older than `SeenPacketCacheDuration` may be replayed.
   This is acceptable for a short demo session.

## Known Limitations

- No full cryptographic verification of relay nodes' identity mid-route (only origin is verified).
- E2E encryption not wired end-to-end into the message send path in this build.
- No revocation mechanism for compromised `NodeId`.
- `ChallengeSecret` in `HandshakeVerifier` is generated once per process start and not used in
  a challenge-response flow in the current implementation — reserved for future hardening.

## What to Say to Judges

The system provides:
- **Anti-spoofing**: fingerprint-bound node identity checked at handshake.
- **Replay protection**: `PacketId`-based deduplication with TTL.
- **Loop/storm protection**: hop-count limit and "never relay back to sender" rule.
- **E2E encryption architecture**: ECDH + AES-256-GCM service exists and can be enabled per session.

For a production system, the next steps would be: persistent key storage, full E2E integration,
certificate-based trust instead of deterministic fingerprints, and a proper challenge-response
handshake.
