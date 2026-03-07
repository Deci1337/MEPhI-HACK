# PERSON_2 — Transport: приём Chat через TransportAdapter

**Цель:** Входящие Chat-пакеты (legacy и Relay) должны попадать в PacketRouter для дедупликации и корректной обработки.

**Граница:** Только `HexTeam.Messenger.Core/Transport`. Не трогать `MainPage`, `PacketRouter`, `HexChatService`, `RelayService`.

---

## Проблема

`TransportAdapter` обрабатывает только `TransportPacketType.Relay`.  
Пакеты `TransportPacketType.Chat` от старого пути идут в `TcpChatTransport` и не проходят через `PacketRouter`.  
В результате при смешанном использовании (один клиент — новый, другой — старый) нет единой дедупликации и Ack.

---

## Задача 1 — Обработка Chat в TransportAdapter

**Файл:** `HexTeam.Messenger.Core/Transport/TransportAdapter.cs`

В `OnTransportEnvelopeReceived` добавить ветку для `TransportPacketType.Chat`:

```csharp
private Task OnTransportEnvelopeReceived(string fromPeerNodeId, TransportEnvelope transportEnvelope)
{
    if (transportEnvelope.Type == TransportPacketType.Relay)
    {
        // existing logic: deserialize Envelope, invoke PacketReceived
        ...
    }
    else if (transportEnvelope.Type == TransportPacketType.Chat)
    {
        TryEmitChatAsEnvelope(fromPeerNodeId, transportEnvelope);
    }
    return Task.CompletedTask;
}

private void TryEmitChatAsEnvelope(string fromPeerNodeId, TransportEnvelope transportEnvelope)
{
    try
    {
        var msg = JsonSerializer.Deserialize<TransportChatMessage>(transportEnvelope.Payload);
        if (msg == null || !Guid.TryParse(fromPeerNodeId, out var fromGuid)) return;

        var chatPacket = new ChatPacket
        {
            MessageId = Guid.TryParse(msg.MessageId, out var mid) ? mid : Guid.NewGuid(),
            Text = msg.Text ?? string.Empty,
            SentAtUtc = msg.TimestampUtc > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(msg.TimestampUtc)
                : DateTimeOffset.UtcNow
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(chatPacket);
        var envelope = new Envelope
        {
            PacketId = Guid.TryParse(transportEnvelope.PacketId, out var pid) ? pid : Guid.NewGuid(),
            MessageId = chatPacket.MessageId,
            SessionId = Guid.Empty, // legacy Chat: session unknown
            OriginNodeId = Guid.TryParse(transportEnvelope.SourceNodeId, out var oid) ? oid : fromGuid,
            CurrentSenderNodeId = fromGuid,
            TargetNodeId = Guid.TryParse(transportEnvelope.DestinationNodeId, out var tid) ? tid : Guid.Empty,
            HopCount = transportEnvelope.HopCount,
            MaxHops = transportEnvelope.MaxHops > 0 ? transportEnvelope.MaxHops : 5,
            PacketType = PacketType.ChatEnvelope,
            Payload = payload
        };
        PacketReceived?.Invoke(envelope, fromGuid);
    }
    catch { /* drop malformed */ }
}
```

Нужен `using HexTeam.Messenger.Core.Protocol;` и `using System.Text.Json;`.

---

## Задача 2 — Не дублировать обработку Chat в TcpChatTransport

**Файл:** `HexTeam.Messenger.Core/Transport/TcpChatTransport.cs`

Сейчас `OnEnvelopeReceived` обрабатывает `TransportPacketType.Chat` и вызывает `MessageReceived`.  
После изменений PERSON_2 пакет пойдёт в TransportAdapter -> PacketRouter -> `ChatMessageReceived` -> `TcpChatTransport.RaiseMessageReceived`.

**Вариант A (рекомендуемый):** Оставить обработку Chat в TcpChatTransport. PacketRouter при получении Core Envelope с ChatEnvelope вызовет `ChatMessageReceived` -> `RaiseMessageReceived`. Значит, Chat, пришедший как Relay, уже поднимется. Chat, пришедший как legacy Chat, теперь тоже пойдёт в TransportAdapter -> PacketRouter -> RaiseMessageReceived. В TcpChatTransport будет двойная обработка: и в HandleChatPacket, и через PacketRouter.

**Вариант B:** Убрать обработку Chat из TcpChatTransport. Все Chat идут только через TransportAdapter -> PacketRouter. Тогда TcpChatTransport.OnEnvelopeReceived не должен вызывать HandleChatPacket для Chat. Удалить `case TransportPacketType.Chat` из switch.

**Рекомендация:** Вариант B — убрать Chat из TcpChatTransport, чтобы один путь.

---

## Задача 3 — RelayForwarder и Relay-пакеты

**Файл:** `HexTeam.Messenger.Core/Transport/RelayForwarder.cs`

Проверить, что RelayForwarder не ломает Relay-пакеты с Core Envelope.  
RelayForwarder работает с `TransportEnvelope` и пересылает по `DestinationNodeId`.  
Убедиться, что при пересылке `Payload` (сериализованный Core Envelope) не меняется.

---

## Задача 4 — Тест

**Файл:** `HexTeam.Messenger.Tests/TransportAdapterTests.cs` (если нет — создать)

- `Chat_packet_converted_to_Envelope_and_emits_PacketReceived`
- `Relay_packet_still_works_as_before`

---

## Контракт от PERSON_1

- `HexChatService` шлёт Core Envelope через `ITransport.SendAsync`.
- `TransportAdapter.SendAsync` упаковывает Envelope в `TransportPacketType.Relay` и шлёт через `PeerConnectionService`.

---

## Контракт для PERSON_3

- Приём сообщений — по-прежнему через `TcpChatTransport.MessageReceived` (PacketRouter вызывает RaiseMessageReceived).
- PERSON_3 не меняет подписку на приём, только источник отправки.

---

## Порядок выполнения

1. Задача 1 (TransportAdapter)
2. Задача 2 (TcpChatTransport — убрать Chat)
3. Задача 3 (проверка RelayForwarder)
4. Задача 4 (тесты)

---

## Проверка

```bash
dotnet test MassangerMaximka/HexTeam.Messenger.Tests/ --filter "TransportAdapter"
dotnet build MassangerMaximka/MassangerMaximka/
```
