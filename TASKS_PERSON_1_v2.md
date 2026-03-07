# PERSON_1 — Core / Protocol / Reliability — Sprint 2

Граница: только `HexTeam.Messenger.Core` + тесты. Не трогать `MainPage`, XAML, `MauiProgram`.

---

## Задача 1 — Зарегистрировать ITransport (CRITICAL)

**Файл:** `MassangerMaximka/HexTeam.Messenger.Core/ServiceCollectionExtensions.cs`

**Проблема:** `RelayService`, `RetryPolicy`, `MessageSyncService` зависят от `ITransport`, но `ITransport`
нигде не регистрируется в DI. Это значит что `PacketRouter` и reconnect/sync **никогда не работают**.

**Файл для проверки:** `HexTeam.Messenger.Core/Abstractions/ITransport.cs` — посмотреть сигнатуру.
Скорее всего надо зарегистрировать `TcpChatTransport` или сделать adapter-обёртку.

**Что сделать:**
```csharp
// В AddHexMessengerCore, после регистрации TcpChatTransport:
services.AddSingleton<ITransport>(sp =>
    new TransportAdapter(sp.GetRequiredService<TcpChatTransport>(),
                         sp.GetRequiredService<PeerConnectionService>()));
```

Если `ITransport.SendAsync(Envelope, Guid, CancellationToken)` принимает Core `Envelope`, а
`TcpChatTransport` работает с `TransportEnvelope` — нужен `TransportAdapter` в
`HexTeam.Messenger.Core/Transport/TransportAdapter.cs` который сериализует Core `Envelope`
в `TransportEnvelope.Payload` и отправляет через `PeerConnectionService.SendAsync`.

---

## Задача 2 — Подключить PacketRouter к входящим пакетам

**Файлы:**
- `HexTeam.Messenger.Core/Transport/TcpChatTransport.cs`
- `HexTeam.Messenger.Core/ServiceCollectionExtensions.cs`

**Проблема:** `PacketRouter` полностью готов, но нигде не вызывается. Входящие пакеты
обрабатываются напрямую в `TcpChatTransport.OnEnvelopeReceived` и `FileTransferService.OnEnvelopeReceived`
минуя всю логику relay / dedup / hop-check.

**Что сделать:**

1. В `TcpChatTransport.OnEnvelopeReceived` — когда тип конверта `Relay` или любой protocol-level тип,
   делегировать в `PacketRouter.HandleIncomingAsync` вместо прямой обработки.

2. Добавить `PacketRouter` в `ServiceCollectionExtensions` после уже существующей регистрации:
```csharp
// уже есть, но надо убедиться что PacketRouter.ChatMessageReceived -> TcpChatTransport.MessageReceived
services.AddSingleton(sp => {
    var router = sp.GetRequiredService<PacketRouter>();
    var chat = sp.GetRequiredService<TcpChatTransport>();
    router.ChatMessageReceived += msg => chat.RaiseMessageReceived(msg); // или через event bridge
    return router;
});
```

---

## Задача 3 — Подключить reconnect/sync к PeerConnected

**Файлы:**
- `HexTeam.Messenger.Core/Transport/PeerConnectionService.cs`
- `HexTeam.Messenger.Core/Services/PacketRouter.cs`
- `HexTeam.Messenger.Core/ServiceCollectionExtensions.cs`

**Проблема:** `PacketRouter.OnPeerReconnectedAsync` написан и отправляет `InventoryPacket`, но
`PeerConnectionService.PeerConnected` никогда не вызывает его.

**Что сделать в `ServiceCollectionExtensions` при создании `PacketRouter`:**
```csharp
services.AddSingleton(sp => {
    var router = new PacketRouter(...);
    var connections = sp.GetRequiredService<PeerConnectionService>();

    connections.PeerConnected += nodeId => {
        // определить sessionId для этого пира (из истории или создать новый)
        var sessionId = sp.GetRequiredService<IMessageStore>().GetSessionIdForPeer(nodeId)
                        ?? Guid.Empty;
        if (sessionId != Guid.Empty)
            _ = router.OnPeerReconnectedAsync(Guid.Parse(nodeId), sessionId);
    };
    return router;
});
```

Если `IMessageStore` не хранит `sessionId → nodeId` mapping, добавить метод
`GetSessionIdForPeer(string nodeId)` в `InMemoryMessageStore`.

---

## Задача 4 — Завершить RetryPolicy и добавить Failed state

**Файл:** `HexTeam.Messenger.Core/Services/RetryPolicy.cs`

**Проверить:**
- Есть ли `MaxRetries` константа и она соблюдается
- После исчерпания ретраев пакет переходит в состояние `Failed` и удаляется из очереди
- `Acknowledge(packetId)` корректно останавливает таймер

**Что добавить если нет:**
```csharp
private const int MaxRetries = 3;
private const int RetryIntervalMs = 2000;

// При исчерпании:
if (entry.AttemptCount >= MaxRetries) {
    _pending.TryRemove(packetId, out _);
    RetryExhausted?.Invoke(packetId); // новый event
    return;
}
```

Добавить `event Action<Guid>? RetryExhausted` — чтобы PERSON_2 мог показать в UI статус Failed.

---

## Задача 5 — Интегрировать TrafficEncryptor в TcpChatTransport

**Файлы:**
- `HexTeam.Messenger.Core/Security/TrafficEncryptor.cs` — уже реализован AES-256-GCM
- `HexTeam.Messenger.Core/Transport/TcpChatTransport.cs`
- `HexTeam.Messenger.Core/Security/KeyExchangeService.cs`

**Проблема:** `TrafficEncryptor` реализован, но TCP-трафик идёт в открытом виде.
`WriteEncryptedFrameAsync` / `ReadEncryptedFrameAsync` не используются.

**Что сделать:**

1. После успешного `Hello`-handshake в `PeerConnectionService` — выполнить ключевой обмен:
   - Отправить свой ECDH публичный ключ
   - Получить публичный ключ пира
   - Через `KeyExchangeService.DeriveSharedSecret(peerPublicKey)` получить AES-ключ

2. Хранить per-peer AES-ключ в `PeerConnection`:
```csharp
public byte[]? SessionKey { get; set; } // добавить в PeerConnection
```

3. В `EnvelopeSerializer.WriteToStreamAsync` / `ReadFromStreamAsync` — если `SessionKey != null`,
   оборачивать через `TrafficEncryptor`.

**Важно:** не ломать `Hello`-пакет (он идёт до обмена ключами, без шифрования).

---

## Задача 6 — Добить тесты

**Файл:** `HexTeam.Messenger.Tests/`

Тесты уже есть. Нужно проверить что они проходят и добавить недостающие:

**Запустить:**
```
dotnet test MassangerMaximka/HexTeam.Messenger.Tests/
```

**Добавить тесты если нет:**
- `RetryPolicy: packet marked Failed after MaxRetries`
- `RetryPolicy: RetryExhausted event fires after exhaustion`
- `PacketRouter: reconnect triggers inventory send`
- `MessageSyncService: ResendMessages only sends known IDs`
- `PacketRateLimiter: blocks peer after 100 packets in 10s`

---

## Задача 7 — Добавить GetSessionIdForPeer в IMessageStore

**Файлы:**
- `HexTeam.Messenger.Core/Storage/IMessageStore.cs`
- `HexTeam.Messenger.Core/Storage/InMemoryMessageStore.cs`

Нужно для Задачи 3. Добавить метод:
```csharp
// IMessageStore
Guid? GetSessionIdForPeer(string nodeId);

// InMemoryMessageStore — вернуть sessionId из первого сообщения с этим пиром
public Guid? GetSessionIdForPeer(string nodeId) =>
    _messages.Values
        .Where(m => m.SenderNodeId.ToString() == nodeId)
        .Select(m => (Guid?)m.SessionId)
        .FirstOrDefault();
```

---

## Порядок выполнения

1. Задача 7 (нужна для 3)
2. Задача 1 (нужна для 2, 3)
3. Задача 2 и 3 параллельно
4. Задача 4
5. Задача 5
6. Задача 6 (тесты в конце)

## Файлы которые нельзя трогать

- `MainPage.xaml`, `MainPage.xaml.cs`
- `MauiProgram.cs`
- `AppShell.xaml`
- `VoiceCallManager.cs`
- `UdpVoiceTransport.cs`
