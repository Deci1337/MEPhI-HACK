# Person 1: План действий на следующие часы

## Анализ текущего состояния

### ✅ Что уже реализовано (хорошо)

1. **Core Protocol Components:**
   - ✅ `Envelope` с поддержкой HopCount, MaxHops, PacketId, MessageId
   - ✅ `RelayService` с deduplication через `ISeenPacketStore`
   - ✅ `RetryPolicy` с Ack/retry/timeout механизмом
   - ✅ `MessageSyncService` с Inventory/MissingRequest протоколом
   - ✅ `InMemorySeenPacketStore` с TTL и автоматической очисткой
   - ✅ `PacketRouter` с обработкой всех типов пакетов
   - ✅ Валидация пакетов в `PacketRouter.ValidateEnvelope`

2. **Tests Coverage:**
   - ✅ RelayServiceTests (6 тестов)
   - ✅ RetryPolicyTests (6 тестов)
   - ✅ MessageSyncServiceTests (6 тестов)
   - ✅ SeenPacketStoreTests
   - ✅ EnvelopeTests

3. **Protocol Constants:**
   - ✅ MaxHops = 5
   - ✅ MaxRetryCount = 3
   - ✅ AckTimeout = 5 секунд
   - ✅ RequiresAck для ChatEnvelope, FileChunk, FileResumeRequest

### ⚠️ Что нужно доработать (критично)

1. **Автоматический reconnect/sync flow:**
   - ❌ `OnPeerReconnectedAsync` вызывается только вручную
   - ❌ Нет автоматического триггера при обнаружении reconnect
   - ❌ Нет обработки случая, когда reconnect происходит во время активной сессии

2. **Порядок обработки сообщений при sync:**
   - ⚠️ Нет гарантии порядка доставки старых сообщений после sync
   - ⚠️ Новые сообщения могут прийти раньше, чем завершится sync старых

3. **Failure handling:**
   - ⚠️ Нет явной обработки битых payload в некоторых местах
   - ⚠️ Нет логирования причин reject для diagnostics

4. **Тесты для reconnect flow:**
   - ❌ Нет интеграционного теста полного цикла: disconnect -> reconnect -> inventory -> missing request -> resend -> dedup

5. **Документация протокола:**
   - ⚠️ PROTOCOL_SPEC.md есть, но нужно проверить актуальность
   - ❌ Нет явного описания protocol invariants

## Приоритетный план действий

### 🔴 КРИТИЧНО (следующие 2-4 часа)

#### 1. Завершить автоматический reconnect/sync flow

**Проблема:** Сейчас `PacketRouter.OnPeerReconnectedAsync` вызывается только вручную. Нужно интегрировать его с transport layer.

**Решение:**
- Добавить в `PacketRouter` метод `OnPeerConnectedAsync(Guid peerNodeId, Guid? existingSessionId)`
- Если `existingSessionId != null`, автоматически запускать sync
- Добавить событие `PeerReconnected` для уведомления UI
- Убедиться, что sync не запускается параллельно для одного peer

**Файлы для изменения:**
- `HexTeam.Messenger.Core/Services/PacketRouter.cs`
- Возможно, добавить интерфейс `IPeerConnectionObserver` для интеграции с transport

**Тест:**
```csharp
[Fact]
public async Task Reconnect_triggers_automatic_inventory_sync()
{
    // Arrange: simulate disconnect and reconnect
    // Act: call OnPeerConnectedAsync with existing session
    // Assert: inventory was sent automatically
}
```

#### 2. Улучшить порядок обработки сообщений при sync

**Проблема:** При sync старые сообщения могут прийти в неправильном порядке или смешаться с новыми.

**Решение:**
- Добавить в `ChatMessage` поле `SyncSequenceNumber` (nullable)
- При обработке `MissingRequest` -> `ResendMessagesAsync` устанавливать `SyncSequenceNumber`
- В `HandleChat` проверять: если сообщение из sync, обрабатывать в порядке `SyncSequenceNumber`
- Добавить очередь для sync-сообщений, если они приходят не по порядку

**Альтернативное простое решение:**
- В `ResendMessagesAsync` отправлять сообщения в порядке `SentAtUtc`
- Добавить небольшую задержку между отправками (50-100ms) для гарантии порядка
- В `HandleChat` при обработке sync-сообщений проверять, что они не дублируются

**Файлы для изменения:**
- `HexTeam.Messenger.Core/Services/MessageSyncService.cs`
- `HexTeam.Messenger.Core/Models/ChatMessage.cs` (опционально)
- `HexTeam.Messenger.Core/Services/PacketRouter.cs`

#### 3. Добавить интеграционный тест reconnect flow

**Цель:** Доказать, что полный цикл reconnect работает корректно.

**Тест должен покрывать:**
1. Node A отправляет сообщения Node B
2. Node B отключается
3. Node A отправляет еще сообщения (они должны быть в pending)
4. Node B подключается обратно
5. Node B получает Inventory от Node A
6. Node B отправляет MissingRequest
7. Node A пересылает пропущенные сообщения
8. Node B получает все сообщения без дублей
9. Порядок сообщений корректный

**Файл:** `HexTeam.Messenger.Tests/ReconnectFlowTests.cs`

### 🟡 ВАЖНО (следующие 4-6 часов)

#### 4. Улучшить failure handling и валидацию

**Задачи:**
- Добавить более детальное логирование причин reject в `ValidateEnvelope`
- Добавить метрики для rejected packets (для diagnostics screen)
- Улучшить обработку битых payload в `HandleChat`, `HandleAck`, `HandleInventory`
- Добавить timeout для sync операций (чтобы не зависать на битых peers)

**Файлы:**
- `HexTeam.Messenger.Core/Services/PacketRouter.cs`
- Возможно, добавить `IRejectionLogger` интерфейс

#### 5. Зафиксировать protocol invariants

**Создать документ:** `PROTOCOL_INVARIANTS.md`

**Содержание:**
- Список всех packet types и их обязательных полей
- Правила для relay (когда forward, когда drop)
- Правила для Ack (какие типы требуют Ack)
- Правила для sync (когда отправлять Inventory)
- Гарантии порядка доставки
- Ограничения (MaxHops, MaxRetryCount, TTL)

**Цель:** Явный контракт для Person 2 и Person 3, чтобы не ломать протокол.

### 🟢 ЖЕЛАТЕЛЬНО (если останется время)

#### 6. Оптимизация seen packet store

**Идея:** Сейчас используется `ConcurrentDictionary<Guid, DateTimeOffset>`. Для больших сетей может быть узким местом.

**Улучшения:**
- Добавить метрики размера store
- Добавить предупреждение, если store превышает разумный размер (например, 10K пакетов)
- Рассмотреть LRU cache вместо простого TTL

#### 7. Дополнительные edge case тесты

- Тест: пакет приходит с HopCount = MaxHops (должен быть dropped)
- Тест: пакет приходит дважды с разными PacketId, но одинаковым MessageId (должен быть dedup на уровне MessageId)
- Тест: sync запускается дважды параллельно для одного peer (должен быть защищен от race condition)

## Конкретные следующие шаги (прямо сейчас)

### Шаг 1: Автоматический reconnect trigger (30-45 минут)

1. Открыть `PacketRouter.cs`
2. Добавить метод:
```csharp
public async Task OnPeerConnectedAsync(Guid peerNodeId, Guid? existingSessionId, CancellationToken ct = default)
{
    if (existingSessionId.HasValue)
    {
        await OnPeerReconnectedAsync(peerNodeId, existingSessionId.Value, ct);
    }
}
```

3. Добавить проверку на параллельный sync:
```csharp
private readonly ConcurrentDictionary<Guid, Task> _activeSyncs = new();

public async Task OnPeerReconnectedAsync(Guid peerNodeId, Guid sessionId, CancellationToken ct = default)
{
    if (_activeSyncs.TryGetValue(peerNodeId, out var existingSync))
    {
        await existingSync; // Wait for existing sync to complete
        return;
    }
    
    var syncTask = _sync.SendInventoryAsync(sessionId, peerNodeId, ct);
    _activeSyncs[peerNodeId] = syncTask;
    
    try
    {
        await syncTask;
        _logger.LogInformation("Sent inventory to reconnected peer {Peer} for session {Session}",
            peerNodeId, sessionId);
    }
    finally
    {
        _activeSyncs.TryRemove(peerNodeId, out _);
    }
}
```

### Шаг 2: Тест для reconnect flow (30 минут)

Создать `ReconnectFlowTests.cs` с полным интеграционным тестом.

### Шаг 3: Улучшить порядок sync сообщений (45 минут)

В `MessageSyncService.ResendMessagesAsync`:
- Сортировать сообщения по `SentAtUtc`
- Добавить небольшую задержку между отправками (или использовать Task.Delay с cancellation)

## Метрики успеха

Следующий этап считается завершенным, если:

1. ✅ При reconnect автоматически запускается sync
2. ✅ Sync сообщения приходят в правильном порядке
3. ✅ Нет дублей после sync
4. ✅ Есть интеграционный тест, который это доказывает
5. ✅ Protocol invariants документированы
6. ✅ Failure handling улучшен с детальным логированием

## Коммиты

Рекомендуемые commit messages:
- `feat: add automatic reconnect sync trigger`
- `feat: ensure sync messages are sent in order`
- `test: add integration test for reconnect flow`
- `feat: improve packet validation and failure logging`
- `docs: add protocol invariants documentation`

## Координация с командой

**Важно согласовать с Person 2:**
- Когда transport layer должен вызывать `OnPeerConnectedAsync`
- Как передавать `existingSessionId` при reconnect

**Важно согласовать с Person 3:**
- Какие метрики нужны для diagnostics screen
- Какие события нужны для UI (например, `PeerReconnected`, `SyncStarted`, `SyncCompleted`)

## Риски и ограничения

1. **Время:** Если осталось меньше 4 часов до demo, сосредоточиться только на шагах 1-2 (автоматический reconnect + тест)

2. **Сложность sync порядка:** Если сложно реализовать гарантию порядка, можно упростить: просто логировать предупреждение, если сообщения приходят не по порядку

3. **Интеграция с transport:** Если Person 2 еще не готов к интеграции, можно сделать mock-версию для тестов

## Дополнительные ресурсы

- См. `PERSON_1_NEXT_TASKS.md` для общего контекста
- См. `PROTOCOL_SPEC.md` для текущей спецификации протокола
- См. `TEAM_TASK_DISTRIBUTION.md` для границ ответственности
