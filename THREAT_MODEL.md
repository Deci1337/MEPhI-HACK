# Threat Model -- HEX.TEAM Offline Messenger

## System Overview

Decentralized P2P messenger for LAN (Wi-Fi/Hotspot). No central server.
Nodes discover each other via UDP broadcast and communicate over TCP.
Messages can be relayed through intermediate nodes (store-and-forward).

## Assets (what we protect)

| Asset | Value |
|-------|-------|
| Message content | Confidentiality of user conversations |
| Node identity | Authenticity of message sender |
| Message integrity | Messages not altered in transit |
| Service availability | Nodes can exchange messages when reachable |
| Message history | Stored messages not leaked to unauthorized parties |

## Threat Actors

| Actor | Capability | Motivation |
|-------|-----------|------------|
| Passive eavesdropper | Sniff LAN traffic | Read private messages |
| Malicious relay node | Participates in network | Modify, drop, or inject messages |
| Spammer / flooder | Send many packets | Deny service to other nodes |
| Spoofing attacker | Forge source identity | Impersonate another user |

## Threats and Mitigations (STRIDE)

### Spoofing (Identity)

| Threat | Mitigation | Status |
|--------|-----------|--------|
| Node impersonation via forged NodeId | `HandshakeVerifier`: fingerprint = SHA256(GUID), verified on connect | DONE |
| Packet origin forgery | `ValidateOrigin`: reject empty origin, reject self-origin with hop=0 | DONE |
| Self-connection loop | `HandshakeVerifier` rejects `hello.NodeId == localNodeId` | DONE |
| Protocol version mismatch | Version check in handshake | DONE |

### Tampering (Integrity)

| Threat | Mitigation | Status |
|--------|-----------|--------|
| Message modification in transit | AES-256-GCM authenticated encryption (TrafficEncryptor) -- tag verification detects any bit flip | DONE |
| Replay of old packets | `InMemorySeenPacketStore` deduplication by PacketId | DONE |
| Tampered relay envelope | GCM tag covers entire frame; any modification fails decryption | DONE |

### Repudiation

| Threat | Mitigation | Status |
|--------|-----------|--------|
| Deny sending a message | MessageId + SenderNodeId stored locally; not cryptographic non-repudiation | PARTIAL |

### Information Disclosure

| Threat | Mitigation | Status |
|--------|-----------|--------|
| Eavesdropping on LAN | Traffic encryption: ECDH key exchange + AES-256-GCM per connection | DONE |
| Relay node reads message content | E2E encryption: per-session ECDH + AES-256-GCM; relay sees only ciphertext | DONE |
| Key compromise | Ephemeral ECDH keys per session; forward secrecy | DONE |

### Denial of Service

| Threat | Mitigation | Status |
|--------|-----------|--------|
| Packet flood from single peer | `PacketRateLimiter`: sliding window per peer (100 pkt / 10s default) | DONE |
| Hop amplification (packet storm) | `MaxHops = 5` + deduplication + "do not send back to sender" | DONE |
| Relay queue exhaustion | `RelayQueueCapacity = 256` bound | DONE |

### Elevation of Privilege

| Threat | Mitigation | Status |
|--------|-----------|--------|
| No privilege model (all nodes are equal) | By design: P2P mesh, no admin role | N/A |

## What We Do NOT Protect Against

- **Compromised device**: if the OS or app is compromised, secrets are exposed.
- **Side-channel attacks**: timing, power analysis, etc.
- **Long-term identity persistence**: nodes use ephemeral GUIDs, not PKI certificates.
- **Global passive adversary**: an attacker who can correlate all LAN traffic metadata.
- **Physical access**: if attacker has physical access to the device.

## Cryptographic Primitives

| Purpose | Algorithm | Key size |
|---------|-----------|----------|
| Key exchange | ECDH (NIST P-256) | 256-bit |
| Key derivation | HKDF-SHA256 | 256-bit output |
| Traffic encryption | AES-256-GCM | 256-bit key, 96-bit nonce, 128-bit tag |
| E2E message encryption | AES-256-GCM | 256-bit key, 96-bit nonce, 128-bit tag |
| Node fingerprint | SHA-256 truncated to 32-bit hex | 8-char fingerprint |

## Summary

The system provides:
1. **Traffic encryption** -- all TCP traffic is encrypted with AES-256-GCM using ECDH-derived keys.
2. **Node authentication** -- fingerprint-based identity verification during handshake.
3. **Anti-spoofing** -- origin validation, self-connection rejection, protocol version check.
4. **Anti-spam** -- per-peer rate limiting with violation tracking.
5. **E2E encryption** -- relay nodes cannot read message content; only endpoints can decrypt.
6. **Forward secrecy** -- ephemeral ECDH keys per session; past traffic cannot be decrypted if current keys are compromised.
