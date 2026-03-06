# Protocol Specification -- HEX.TEAM Messenger v1.0

## Status: FROZEN

Changes to these contracts require team-wide agreement.

---

## 1. Envelope (transport unit)

Every packet traverses the network inside an `Envelope`:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| PacketId | Guid | yes | Globally unique per packet instance |
| MessageId | Guid | yes | Logical message id (may span retries) |
| SessionId | Guid | yes | Conversation / session scope |
| OriginNodeId | Guid | yes | Node that originally created the packet |
| CurrentSenderNodeId | Guid | yes | Last node that forwarded the packet |
| TargetNodeId | Guid | yes | Destination node (Guid.Empty = broadcast) |
| HopCount | int | yes | Incremented on each relay hop, starts at 0 |
| MaxHops | int | yes | Upper bound, default = 5 |
| CreatedAtUtc | DateTimeOffset | yes | Timestamp of packet creation |
| PacketType | PacketType (byte) | yes | Discriminator for payload |
| Payload | byte[] | yes | JSON-serialized packet body |

### Invariants

- `HopCount < MaxHops` -- otherwise packet is dropped (DropHopExceeded).
- `OriginNodeId != Guid.Empty`.
- `PacketId` must be unique; duplicates are silently dropped (DropDuplicate).
- Relay nodes increment `HopCount` and update `CurrentSenderNodeId`.
- Relay nodes never send a packet back to `receivedFromNodeId` or `OriginNodeId`.

---

## 2. PacketType enum

| Value | Name | Ack required | Description |
|-------|------|-------------|-------------|
| 1 | Hello | no | Handshake identification |
| 2 | PeerAnnounce | no | Peer discovery broadcast |
| 3 | SessionOpen | no | Session establishment |
| 4 | ChatEnvelope | yes | Chat message payload |
| 5 | Ack | no | Delivery acknowledgment |
| 6 | Inventory | no | Message ID list for sync |
| 7 | MissingRequest | no | Request to resend specific messages |
| 8 | FileChunk | yes | File transfer data chunk |
| 9 | FileChunkAck | no | Acknowledgment for file chunk |
| 10 | FileResumeRequest | yes | Request to resume file transfer |
| 11 | VoiceStart | no | Voice call initiation |
| 12 | VoiceFrame | no | Voice audio data |
| 13 | VoiceStop | no | Voice call termination |
| 14 | Ping | no | Keepalive / latency probe |
| 15 | QualityReport | no | Network quality metrics |

---

## 3. Payload contracts

### ChatPacket
```
{ MessageId: Guid, Text: string, SentAtUtc: DateTimeOffset }
```

### AckPacket
```
{ AckedPacketId: Guid, AckedMessageId: Guid, Status: AckStatus(Delivered|Read|Failed) }
```

### InventoryPacket
```
{ SessionId: Guid, MessageIds: List<Guid>, SinceUtc: DateTimeOffset }
```

### MissingRequestPacket
```
{ SessionId: Guid, MissingMessageIds: List<Guid> }
```

### HelloPacket
```
{ NodeId: Guid, DisplayName: string, Fingerprint: string, ListenPort: int, ProtocolVersion: "1.0" }
```

---

## 4. Reliability rules

### Ack/Retry
- Packets with `RequiresAck = true` are tracked by `RetryPolicy`.
- `AckTimeout` = 5 seconds.
- `MaxRetryCount` = 3.
- After max retries exhausted, delivery state transitions to `Failed`.
- Only the originating node retries; relay nodes do not retry.

### Deduplication
- Single source of truth: `ISeenPacketStore` keyed by `PacketId`.
- TTL for seen cache: 300 seconds.
- Automatic periodic pruning every 60 seconds.
- Message-level dedup in `IMessageStore.Contains(MessageId)` prevents UI duplication.

### Loop protection
- `HopCount >= MaxHops` -> drop.
- Relay never sends back to `receivedFromNodeId`.
- Relay never sends to `OriginNodeId`.

---

## 5. Reconnect / Sync protocol

1. On peer reconnect, node sends `Inventory` packet listing known `MessageIds` for the session.
2. Receiving node compares with local store, computes missing set.
3. If missing > 0, sends `MissingRequest` back with the list of unknown `MessageIds`.
4. Original node resends the requested `ChatEnvelope` packets.
5. Deduplication ensures already-received messages are not processed twice.

---

## 6. Validation rules (entry point)

At `PacketRouter.HandleIncomingAsync`:
1. Reject if `PacketId == Guid.Empty`.
2. Reject if `ValidateOrigin` fails (empty OriginNodeId, or self-origin with hop 0).
3. Reject if `Payload == null`.
4. Reject if `PacketType` is not a defined enum value.
5. All rejections are logged with reason.
6. Malformed payload deserialization is caught per-handler with logged exception.

---

## 7. Security baseline

| Measure | Implementation | Component |
|---------|---------------|-----------|
| Traffic encryption | AES-256-GCM per TCP stream | `TrafficEncryptor` |
| Key exchange | ECDH (NIST P-256) + HKDF-SHA256 | `KeyExchangeService` |
| Node authentication | SHA256 fingerprint of NodeId, verified at handshake | `HandshakeVerifier` |
| Anti-spoofing | Origin validation, self-connection rejection, protocol version check | `HandshakeVerifier` |
| Anti-spam | Per-peer sliding window rate limiter (100 pkt/10s) | `PacketRateLimiter` |
| E2E encryption | Ephemeral ECDH + AES-256-GCM per session | `E2EEncryptionService` |
| Replay protection | `InMemorySeenPacketStore` deduplication by PacketId | `ISeenPacketStore` |

---

## 8. Constants

| Constant | Value | Purpose |
|----------|-------|---------|
| MaxHops | 5 | Relay depth limit |
| MaxRetryCount | 3 | Ack retry attempts |
| AckTimeout | 5s | Time before retry |
| SeenPacketCacheDuration | 300s | Dedup TTL |
| SeenPacketPruneInterval | 60s | Cleanup frequency |
| FileChunkSize | 64 KB | File transfer chunk |
| RelayQueueCapacity | 256 | Max queued relay packets |
| ProtocolVersion | "1.0" | Handshake version check |
