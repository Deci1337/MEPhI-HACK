# PERSON_1 — Core: HexChatService и путь через PacketRouter

**Цель:** Чат должен идти через Core-протокол (Relay, Retry, Ack, Sync), а не только через TcpChatTransport.

**Граница:** Только `HexTeam.Messenger.Core`. Не трогать `MainPage`, XAML, `TcpChatTransport`, `PeerConnectionService`.

---

## Проблема

Сейчас чат отправляется через `TcpChatTransport.SendMessageAsync` (TransportPacketType.Chat).  
`PacketRouter`, `RetryPolicy`, `MessageSyncService` обрабатывают только Relay-пакеты с Core Envelope.  
В итоге Retry, Ack, Sync и дедупликация Core-протокола не применяются к чату.

---

## Задача 1 — Создать IHexChatService и HexChatService

**Файлы:**
- `HexTeam.Messenger.Core/Abstractions/IHexChatService.cs` (новый)
- `HexTeam.Messenger.Core/Services/HexChatService.cs` (новый)

**Контракт (должен совпадать с ожиданиями PERSON_3):**

```csharp
// Abstractions/IHexChatService.cs
public interface IHexChatService
{
    event Action<string, DeliveryStatus>? DeliveryStatusChanged;
    Task<TransportChatMessage> SendMessageAsync(string toNodeId, string text, CancellationToken ct = default);
}
```

`MessageReceived` не нужен: приём идёт через `TcpChatTransport.MessageReceived` (PacketRouter -> RaiseMessageReceived).

**Реализация:**

1. `SendMessageAsync`:
   - Создать `ChatPacket` (MessageId, Text, SentAtUtc)
   - Создать `Envelope` с `PacketType.ChatEnvelope`, `TargetNodeId` = toNodeId, `OriginNodeId` = local
   - `SessionId` = стабильный ID сессии с этим пиром (можно `Guid.CreateVersion7()` или хэш от пары nodeId)
   - Вызвать `ITransport.SendAsync(envelope, targetGuid, ct)`
   - Вызвать `PacketRouter.TrackForAck(envelope, targetGuid)`
   - Вернуть `TransportChatMessage` с тем же MessageId (строка), Status = Sent

2. Подписаться на `PacketRouter.AckReceived`:
   - При `AckReceived(ackedPacketId, status)` найти соответствующий MessageId (хранить `Dictionary<Guid, string>` pending)
   - Вызвать `DeliveryStatusChanged?.Invoke(messageIdStr, status == AckStatus.Delivered ? DeliveryStatus.Delivered : ...)`

3. Подписаться на `RetryPolicy.RetryExhausted` (если есть):
   - Вызвать `DeliveryStatusChanged?.Invoke(messageIdStr, DeliveryStatus.Failed)`

4. `MessageReceived` — не нужен: приём идёт через `PacketRouter.ChatMessageReceived` -> `TcpChatTransport.RaiseMessageReceived` (уже в ServiceCollectionExtensions). PERSON_3 остаётся подписан на `TcpChatTransport.MessageReceived` для приёма.

---

## Задача 2 — Зарегистрировать HexChatService в DI

**Файл:** `HexTeam.Messenger.Core/ServiceCollectionExtensions.cs`

После регистрации `PacketRouter` добавить:

```csharp
services.AddSingleton<IHexChatService>(sp =>
{
    var svc = new HexChatService(
        sp.GetRequiredService<ITransport>(),
        sp.GetRequiredService<PacketRouter>(),
        sp.GetRequiredService<NodeIdentity>(),
        sp.GetRequiredService<ILogger<HexChatService>>());
    // Wire AckReceived -> DeliveryStatusChanged
    return svc;
});
```

---

## Задача 3 — SessionId для новой сессии

**Файл:** `HexTeam.Messenger.Core/Storage/IMessageStore.cs` и `InMemoryMessageStore.cs`

`GetSessionIdForPeer` возвращает `null`, если нет сообщений. Для первой отправки нужен SessionId.

Добавить в `IMessageStore`:

```csharp
Guid GetOrCreateSessionId(string nodeId);
```

`InMemoryMessageStore`: хранить `ConcurrentDictionary<string, Guid> _sessionByPeer`. Логика:
- если есть сообщения от этого пира — вернуть их SessionId;
- иначе если `_sessionByPeer` содержит — вернуть;
- иначе создать `Guid.CreateVersion7()`, сохранить, вернуть.

---

## Задача 4 — Тесты

**Файл:** `HexTeam.Messenger.Tests/HexChatServiceTests.cs`

- `SendMessageAsync_creates_valid_Envelope_and_calls_ITransport`
- `AckReceived_raises_DeliveryStatusChanged`
- `SessionId_is_stable_for_same_peer`

---

## Контракт для PERSON_2

- `TransportAdapter` должен при получении `TransportPacketType.Relay` десериализовать Core `Envelope` и вызывать `PacketReceived`. (Уже так.)
- При получении `TransportPacketType.Chat` (legacy) — конвертировать в Core Envelope и вызвать `PacketReceived`. (PERSON_2 делает.)

---

## Контракт для PERSON_3

- `IHexChatService.SendMessageAsync(toNodeId, text)` — использовать вместо `TcpChatTransport.SendMessageAsync` для текста.
- `IHexChatService.DeliveryStatusChanged` — подписаться для обновления статуса в UI.
- Приём сообщений — оставить подписку на `TcpChatTransport.MessageReceived` (PacketRouter уже поднимает туда через RaiseMessageReceived).

---

## Порядок выполнения

1. Задача 3 (SessionId)
2. Задача 1 (HexChatService)
3. Задача 2 (DI)
4. Задача 4 (тесты)

---

## Проверка

```bash
dotnet test MassangerMaximka/HexTeam.Messenger.Tests/ --filter "HexChatService"
```
