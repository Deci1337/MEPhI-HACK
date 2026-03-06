# Person 1 Technical Assignment

## Role

`Core + Protocol + Reliability`

Ты отвечаешь за сердце системы: протокол сообщений, relay-логику, надёжность доставки, дедупликацию, защиту от петель и поведение при разрывах.

## Project Context

Команда строит офлайн-децентрализованный мессенджер на `.NET MAUI C#` для:
- `Windows`
- `Android`

Цель проекта:
- работать без интернета;
- находить peers в локальной сети;
- открывать `P2P session`;
- отправлять текстовые сообщения;
- пересылать сообщения и файлы через relay-узел;
- поддерживать file transfer;
- по возможности поддерживать `voice`;
- набрать максимум баллов по ТЗ.

У команды:
- `1 Android`
- `2 Windows`
- `48 часов`

Значит твоя задача не строить сложный mesh, а сделать **простую, надёжную и демонстрируемую relay-логику**.

## Main Objective

Ты должен реализовать минимальный, но сильный протокол, который закрывает:
- `Ack/retry`
- deduplication
- loop protection
- relay forwarding rules
- reconnect/sync
- стабильную обработку локальной истории

Это один из самых важных блоков для базовых баллов ТЗ.

## What Exactly You Own

Ты владеешь только логикой уровня `Core`.

Твоя зона:
- packet contracts
- message contracts
- relay rules
- retry rules
- dedup rules
- reconnect/sync logic
- persistence contracts for message state
- protocol tests

Ты **не** владеешь:
- MAUI UI
- navigation
- platform permissions
- transport implementation details
- README/presentation

## Required Project Area

Твой основной проект:
- `HexTeam.Messenger.Core`

Твой тестовый проект:
- `HexTeam.Messenger.Tests`

## Must Define Early

В первые часы ты должен заморозить:
- `NodeId`
- `PeerInfo`
- `SessionId`
- `MessageId`
- `PacketId`
- `TransferId`
- `HopCount`
- `MaxHops`
- `Ack timeout`
- retry count
- список packet types

После freeze нельзя без согласования ломать DTO, потому что от них зависят все остальные.

## Protocol Model

Каждый пакет должен включать:
- `PacketId`
- `MessageId`
- `SessionId`
- `OriginNodeId`
- `CurrentSenderNodeId`
- `TargetNodeId` или broadcast target
- `HopCount`
- `MaxHops`
- `CreatedAtUtc`
- `PacketType`

Для file transfer:
- `TransferId`
- `ChunkIndex`
- `TotalChunks`
- `ChunkHash` или `FileHash`

## Packet Types You Must Support

Минимально:
- `Hello`
- `PeerAnnounce`
- `SessionOpen`
- `ChatEnvelope`
- `Ack`
- `Inventory`
- `MissingRequest`
- `FileChunk`
- `FileChunkAck`
- `FileResumeRequest`
- `VoiceStart`
- `VoiceFrame`
- `VoiceStop`
- `Ping`
- `QualityReport`

## Core Logic You Must Implement

### 1. Envelope Rules

Нужен единый envelope для всех передаваемых сущностей.

Он должен позволять:
- однозначно идентифицировать пакет;
- отслеживать relay path;
- применять deduplication;
- применять TTL-like ограничение на hops.

### 2. Relay Rules

Нужен **bounded store-and-forward relay**.

Правила:
- forward only if packet unseen;
- forward only if `HopCount < MaxHops`;
- не отправлять назад тому peer, от которого пакет пришёл;
- сохранять state, что пакет уже видели;
- уметь переслать missing packets after reconnect.

### 3. Ack And Retry

Нужно реализовать:
- `Ack`
- timeout
- retry
- bounded retry count
- failure state after max retry exhausted

### 4. Deduplication

Система должна:
- игнорировать дубли;
- не отображать один message дважды в UI;
- не пересылать один и тот же packet повторно по relay-цепочке.

### 5. Loop Protection

Нужно:
- ограничить число hops;
- не допускать циклического relay;
- не допускать packet storm из-за ретрансляции.

### 6. Reconnect And Sync

При reconnect нужно:
- обмениваться inventory;
- запрашивать missing messages;
- догонять историю;
- не плодить дубли после resync.

## Suggested Core Files

Точные имена могут меняться, но примерно:
- `Models/NodeIdentity.cs`
- `Models/PeerInfo.cs`
- `Models/Envelope.cs`
- `Models/ChatMessage.cs`
- `Models/FileTransferInfo.cs`
- `Protocol/PacketType.cs`
- `Protocol/HelloPacket.cs`
- `Protocol/ChatPacket.cs`
- `Protocol/AckPacket.cs`
- `Protocol/InventoryPacket.cs`
- `Protocol/MissingRequestPacket.cs`
- `Protocol/FileChunkPacket.cs`
- `Protocol/VoiceFramePacket.cs`
- `Services/RelayService.cs`
- `Services/RetryPolicy.cs`
- `Services/MessageSyncService.cs`
- `Storage/ISeenPacketStore.cs`
- `Storage/IMessageStore.cs`

## Inputs From Other People

Ты получаешь:
- от Person 2: реальные transport events и delivery callbacks
- от Person 3: UI expectations и connection states

## Outputs For Other People

Ты должен отдать:
- стабильные DTO/contracts
- relay rules
- ack/retry contracts
- service interfaces
- clear result/status enums

Это критично, потому что Person 2 и Person 3 строят работу поверх твоих контрактов.

## Tests You Must Write

Обязательные unit tests:
- duplicate packet ignored
- packet not forwarded twice
- packet dropped when `HopCount >= MaxHops`
- retry starts after timeout
- packet marked failed after max retries
- reconnect requests missing messages
- relay does not send packet back to previous peer
- inventory sync does not duplicate local history

## Milestones For You

### Hours 0-2

- зафиксировать contracts
- зафиксировать packet types
- согласовать retry/ack rules

### Hours 2-8

- сделать envelope logic
- сделать ack/retry
- сделать dedup

### Hours 8-16

- сделать relay rules
- сделать reconnect/sync

### Hours 16-28

- покрыть core behavior тестами
- отловить edge cases

### Hours 28-40

- помочь security baseline:
  - node identity
  - anti-spoofing assumptions

### Hours 40-48

- не добавлять новые крупные фичи
- только stabilization and bugfix

## Done Criteria

Твой блок считается готовым, если:
- contracts заморожены и понятны;
- relay работает без циклов;
- dedup работает;
- retry работает;
- reconnect/sync работает;
- тесты проходят;
- нет расхождения между protocol model и transport/UI.

## What You Must Not Do

Не трать время на:
- MAUI UI
- Android permissions
- video
- BLE / Wi-Fi Direct
- production-grade E2EE
- сложный mesh routing
- рефакторинги ради красоты

## Branch

- `feature/core-protocol`

## Commit Examples

- `feat: add protocol envelope and packet types`
- `feat: implement ack retry state machine`
- `feat: add relay ttl and dedup logic`
- `feat: add reconnect inventory sync`
- `test: cover protocol retry and relay cases`

## Priority Reminder

Твоя задача не написать самый умный протокол, а самый:
- понятный;
- тестируемый;
- устойчивый;
- удобный для demo.

