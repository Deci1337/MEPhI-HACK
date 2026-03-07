# Protocol Invariants

Этот документ описывает **неизменяемые правила протокола**, которые должны соблюдаться всеми компонентами системы. Нарушение этих правил может привести к непредсказуемому поведению, дублированию сообщений, зацикливанию пакетов или другим критическим ошибкам.

## Важность

После заморозки этих правил **нельзя изменять контракты без согласования** с командой, так как от них зависят:
- Transport layer (Person 2)
- UI layer (Person 3)
- Тесты и валидация

## 1. Packet Types и их обязательные поля

### Envelope (базовый контейнер для всех пакетов)

**Обязательные поля:**
- `PacketId: Guid` - уникальный идентификатор пакета (не может быть `Guid.Empty`)
- `MessageId: Guid` - идентификатор сообщения (может быть `Guid.Empty` для служебных пакетов)
- `SessionId: Guid` - идентификатор сессии (обязателен для всех пакетов кроме `Hello` и `PeerAnnounce`)
- `OriginNodeId: Guid` - идентификатор узла-отправителя (не может быть `Guid.Empty`)
- `CurrentSenderNodeId: Guid` - идентификатор текущего отправителя (меняется при relay)
- `TargetNodeId: Guid` - идентификатор целевого узла (`Guid.Empty` для broadcast)
- `HopCount: int` - количество пройденных hops (>= 0)
- `MaxHops: int` - максимальное количество hops (по умолчанию = 5)
- `CreatedAtUtc: DateTimeOffset` - время создания пакета
- `PacketType: PacketType` - тип пакета (должен быть валидным enum значением)
- `Payload: byte[]` - сериализованные данные пакета (не может быть `null`, может быть пустым только для `Ping`)

### ChatPacket (в payload Envelope)

**Обязательные поля:**
- `MessageId: Guid` - идентификатор сообщения (не может быть `Guid.Empty`)
- `Text: string` - текст сообщения (может быть пустым, но не `null`)
- `SentAtUtc: DateTimeOffset` - время отправки сообщения

### AckPacket (в payload Envelope)

**Обязательные поля:**
- `AckedPacketId: Guid` - идентификатор подтверждаемого пакета (не может быть `Guid.Empty`)
- `AckedMessageId: Guid` - идентификатор подтверждаемого сообщения
- `Status: AckStatus` - статус подтверждения (`Delivered`, `Read`, `Failed`)

### InventoryPacket (в payload Envelope)

**Обязательные поля:**
- `SessionId: Guid` - идентификатор сессии (не может быть `Guid.Empty`)
- `MessageIds: List<Guid>` - список идентификаторов сообщений (не может быть `null`, может быть пустым)
- `SinceUtc: DateTimeOffset` - временная метка для фильтрации

### MissingRequestPacket (в payload Envelope)

**Обязательные поля:**
- `SessionId: Guid` - идентификатор сессии (не может быть `Guid.Empty`)
- `MissingMessageIds: List<Guid>` - список идентификаторов отсутствующих сообщений (не может быть `null`, не может быть пустым)

## 2. Правила для Relay

### Когда forward (пересылать пакет)

Пакет должен быть переслан, если выполняются **все** условия:
1. `HopCount < MaxHops` (пакет не исчерпал лимит hops)
2. Пакет еще не был виден (`PacketId` не в `ISeenPacketStore`)
3. `TargetNodeId != LocalNodeId` (пакет не предназначен локальному узлу)
4. `TargetNodeId != Guid.Empty` или это broadcast пакет

### Когда drop (отбросить пакет)

Пакет должен быть отброшен, если выполняется **любое** условие:
1. `HopCount >= MaxHops` → `DropHopExceeded`
2. Пакет уже был виден → `DropDuplicate`
3. `TargetNodeId == LocalNodeId` → `Deliver` (не drop, но не forward)

### Защита от зацикливания

**Критическое правило:** Relay **никогда** не отправляет пакет обратно узлу, от которого он был получен.

```csharp
// Правило: не отправлять обратно receivedFromNodeId
var peers = _transport.GetConnectedPeers()
    .Where(p => p != receivedFromNodeId && p != envelope.OriginNodeId)
    .ToList();
```

**Дополнительная защита:**
- Relay никогда не отправляет пакет обратно `OriginNodeId`
- Пакет помечается как "seen" **до** пересылки
- `HopCount` увеличивается **перед** пересылкой

## 3. Правила для Ack/Retry

### Какие типы пакетов требуют Ack

Только следующие типы пакетов требуют подтверждения:
- `PacketType.ChatEnvelope`
- `PacketType.FileChunk`
- `PacketType.FileResumeRequest`

**Важно:** `AckPacket` сам по себе **не требует** Ack (иначе будет бесконечный цикл).

### Timeout и Retry

**Константы:**
- `AckTimeout = 5 секунд` - время ожидания Ack перед retry
- `MaxRetryCount = 3` - максимальное количество попыток

**Правила:**
1. После отправки пакета, требующего Ack, он добавляется в `RetryPolicy` с состоянием `Waiting`
2. Если Ack не получен в течение `AckTimeout`, пакет переотправляется
3. После `MaxRetryCount` попыток пакет переходит в состояние `Failed`
4. Только **origin node** выполняет retry; relay nodes не retry пересылаемые пакеты

### Состояния доставки

```csharp
public enum MessageDeliveryState
{
    Pending,    // Сообщение еще не отправлено
    Sent,       // Отправлено, ожидается Ack
    Relayed,    // Переслано через relay (для информации)
    Delivered,  // Получен Ack
    Failed      // Исчерпаны все попытки retry
}
```

## 4. Правила для Sync (Reconnect)

### Когда отправлять Inventory

Inventory отправляется автоматически при:
1. Вызове `OnPeerReconnectedAsync(peerNodeId, sessionId)`
2. Обнаружении reconnect peer с существующей сессией

**Важно:** Inventory **не отправляется** при обычном подключении нового peer (только при reconnect).

### Процесс Sync

1. **Node A** отправляет `InventoryPacket` с `MessageIds` всех сообщений в сессии
2. **Node B** получает Inventory и вычисляет missing messages через `FindMissing()`
3. Если есть missing messages, **Node B** отправляет `MissingRequestPacket`
4. **Node A** получает MissingRequest и вызывает `ResendMessagesAsync()`
5. Сообщения переотправляются **в порядке** `SentAtUtc` (старые первыми)
6. Между отправками сообщений добавляется задержка 50ms для гарантии порядка

### Защита от параллельных Sync

**Критическое правило:** Для одного peer может быть активен только один sync процесс одновременно.

Реализация:
```csharp
private readonly ConcurrentDictionary<Guid, Task> _activeSyncs = new();

// Если sync уже идет, ждем его завершения
if (_activeSyncs.TryGetValue(peerNodeId, out var existingSync))
{
    await existingSync;
    return;
}
```

### Deduplication при Sync

- Сообщения проверяются на дубликаты через `IMessageStore.Contains(MessageId)`
- Если сообщение уже есть в store, оно **не добавляется повторно**
- Это гарантирует отсутствие дублей даже при множественных sync

## 5. Гарантии порядка доставки

### Прямая доставка (direct)

Сообщения доставляются в порядке получения пакетов от transport layer.

### Relay доставка

Порядок не гарантируется при relay через промежуточные узлы. Разные пакеты могут идти разными путями.

### Sync доставка

**Гарантия:** При sync сообщения переотправляются в порядке `SentAtUtc` (от старых к новым).

Реализация:
```csharp
var messages = requestedIds
    .Select(msgId => FindMessageById(msgId))
    .Where(msg => msg != null)
    .OrderBy(msg => msg!.SentAtUtc)  // Сортировка по времени
    .ToList();
```

## 6. Ограничения и константы

### ProtocolConstants

```csharp
public const int MaxHops = 5;                    // Максимальное количество hops
public const int MaxRetryCount = 3;              // Максимальное количество retry
public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);  // Timeout для Ack
public const int FileChunkSizeBytes = 64 * 1024; // Размер chunk для файлов
public const string HashAlgorithm = "SHA256";    // Алгоритм хеширования
public const int SeenPacketCacheDuration = 300;  // TTL для seen packets (секунды)
public const int RelayQueueCapacity = 256;       // Емкость очереди relay
public const int SeenPacketPruneIntervalSeconds = 60;  // Интервал очистки seen cache
```

### Валидация значений

**HopCount:**
- Должен быть >= 0
- Если `HopCount >= MaxHops`, пакет отбрасывается

**PacketId:**
- Не может быть `Guid.Empty`
- Должен быть уникальным для каждого пакета

**SessionId:**
- Обязателен для всех пакетов кроме `Hello` и `PeerAnnounce`
- Не может быть `Guid.Empty` для пакетов, требующих сессию

**Payload:**
- Не может быть `null`
- Может быть пустым только для `Ping` пакетов
- Для остальных типов должен содержать валидные сериализованные данные

## 7. Обработка ошибок

### Валидация пакетов

Все входящие пакеты должны проходить валидацию через `ValidateEnvelope()`:
1. Проверка `PacketId != Guid.Empty`
2. Проверка origin через `HandshakeVerifier.ValidateOrigin()`
3. Проверка `Payload != null`
4. Проверка `Payload.Length > 0` (кроме `Ping`)
5. Проверка валидности `PacketType`
6. Проверка `HopCount >= 0`
7. Проверка `SessionId` для пакетов, требующих сессию

### Обработка битых payload

При ошибке десериализации:
1. Логируется предупреждение с деталями (`JsonException` отдельно от других `Exception`)
2. Пакет отбрасывается без crash
3. Ошибка не влияет на обработку других пакетов

### Обработка неизвестных типов пакетов

Неизвестные `PacketType`:
1. Логируются как предупреждение
2. Пакет отбрасывается
3. Не вызывается crash

## 8. Thread Safety

### Потокобезопасные компоненты

- `ISeenPacketStore` - использует `ConcurrentDictionary`
- `IMessageStore` - должен быть потокобезопасным
- `RetryPolicy` - использует `ConcurrentDictionary` для pending packets
- `PacketRouter._activeSyncs` - использует `ConcurrentDictionary`

### Гарантии

- Один пакет обрабатывается одним потоком (нет параллельной обработки одного `PacketId`)
- Relay операции потокобезопасны
- Sync операции защищены от параллельного выполнения для одного peer

## 9. События (Events)

### PacketRouter Events

```csharp
public event Action<ChatMessage>? ChatMessageReceived;
public event Action<Guid, AckStatus>? AckReceived;
public event Action<Envelope>? FilePacketReceived;
public event Action<Envelope>? VoicePacketReceived;
public event Action<RelayDecision, Envelope>? PacketRouted;
public event Action<Guid, Guid>? PeerReconnected;  // (peerNodeId, sessionId)
public event Action<Guid>? SyncStarted;              // (peerNodeId)
public event Action<Guid>? SyncCompleted;            // (peerNodeId)
```

**Правила:**
- События вызываются синхронно в контексте обработки пакета
- Подписчики не должны выполнять долгие операции
- Исключения в обработчиках событий не должны влиять на обработку пакета

## 10. Изменения и версионирование

### Замороженные контракты

После заморозки **нельзя изменять** без согласования:
- Структуру `Envelope`
- Список `PacketType` enum
- Обязательные поля для каждого packet type
- Значения `ProtocolConstants`
- Правила relay и deduplication

### Процесс изменения

1. Обсуждение изменения с командой
2. Обновление этого документа
3. Обновление тестов
4. Миграция существующих данных (если необходимо)

## 11. Тестирование

### Обязательные тесты

Каждое правило должно быть покрыто тестами:

- ✅ `duplicate packet ignored`
- ✅ `packet is not forwarded twice`
- ✅ `packet dropped when HopCount >= MaxHops`
- ✅ `retry starts after timeout`
- ✅ `reconnect requests missing messages`
- ✅ `relay does not send packet back to previous peer`
- ✅ `inventory sync does not duplicate local history`
- ✅ `sync messages arrive in correct order`
- ✅ `reconnect prevents parallel syncs`

## Заключение

Эти правила являются **основой стабильности** протокола. Нарушение любого из них может привести к:
- Дублированию сообщений
- Зацикливанию пакетов
- Потере сообщений
- Непредсказуемому поведению при reconnect
- Проблемам с надежностью доставки

**При изменении любого правила необходимо:**
1. Обновить этот документ
2. Обновить все тесты
3. Уведомить команду
4. Проверить совместимость с существующим кодом
