# Person 1 Next Tasks

## Purpose

Этот файл даёт `PERSON_1` следующий backlog по ТЗ так, чтобы он мог работать **параллельно** с `PERSON_2` без конфликтов по зонам ответственности.

Граница простая:
- `PERSON_1` = `Core + Protocol + Reliability`
- `PERSON_2` = `Network + Transport + File + Voice`
- `PERSON_3` = `MAUI UI + Integration + Demo + Docs`

## Important Non-Overlap Rule

`PERSON_1` не должен брать в работу:
- `UdpDiscoveryService`
- `PeerConnectionService`
- `TcpChatTransport`
- `RelayForwarder` как transport plumbing
- `FileTransferService`
- `UdpVoiceTransport`
- `MetricsService`
- `MainPage`, `MauiProgram`, XAML и прочий UI

Если для его задачи нужны изменения в transport-facing контрактах, он должен:
- сначала согласовать изменение;
- затем менять только contracts/rules, а не transport implementation.

## Main Goal Right Now

Добить то, что усиливает demo и закрывает ТЗ по надёжности:
- `Ack/retry`
- deduplication
- loop protection
- reconnect/sync
- protocol validation
- tests
- security baseline

## Priority Backlog

### 1. Finish Reconnect And Sync

Статус:
- `high priority`

Задачи:
- довести flow `InventoryPacket -> MissingRequestPacket -> resend missing`;
- после reconnect уметь запрашивать пропущенные сообщения;
- не допускать дублей после sync;
- проверить порядок обработки старых и новых сообщений;
- сделать явный сценарий `disconnect -> reconnect -> history catch-up`.

Результат:
- после краткого разрыва связи узел догоняет пропущенные сообщения без дублей.

### 2. Harden Ack Retry Timeout Rules

Статус:
- `high priority`

Задачи:
- зафиксировать, какие packet types требуют `Ack`;
- унифицировать timeout/retry правила;
- ограничить число повторов;
- добавить финальный `Failed` state после исчерпания retry;
- убедиться, что retry-логика живёт в core policy, а не размазана по transport.

Результат:
- надёжность доставки описана едиными правилами и работает предсказуемо.

### 3. Finalize Dedup And Loop Protection

Статус:
- `high priority`

Задачи:
- сделать единый source of truth для `seen packets`;
- определить TTL для seen packet store;
- гарантировать, что пакет не обрабатывается повторно после relay/retry;
- закрепить правило `do not send back to previous hop`;
- гарантировать drop при `HopCount >= MaxHops`.

Результат:
- relay не зацикливается и не размножает пакеты.

### 4. Freeze Protocol Rules

Статус:
- `medium priority`

Задачи:
- финально зафиксировать `Envelope`, `PacketType`, `NodeId`, `PacketId`, `AckTimeout`, `MaxHops`;
- зафиксировать обязательные поля для `Chat`, `Ack`, `Inventory`, `MissingRequest`, `FileResumeRequest`, `VoiceFrame`;
- описать protocol invariants в отдельном коротком документе или внутри existing docs;
- пометить, что после freeze контракты меняются только по согласованию.

Результат:
- у transport и UI стабильная основа, которую можно не переписывать.

### 5. Add Critical Protocol Tests

Статус:
- `high priority`

Обязательные тесты:
- `duplicate packet ignored`
- `packet is not forwarded twice`
- `packet dropped when HopCount >= MaxHops`
- `retry starts after timeout`
- `packet marked failed after max retries`
- `reconnect requests missing messages`
- `relay does not send packet back to previous peer`
- `inventory sync does not duplicate local history`

Результат:
- core behavior доказуем и воспроизводим перед demo.

### 6. Add Failure Handling And Validation

Статус:
- `medium priority`

Задачи:
- безопасно обрабатывать битый payload;
- безопасно обрабатывать unknown packet type;
- валидировать обязательные поля пакета;
- дропать невалидные пакеты без crash;
- логировать причину reject/drop для diagnostics.

Результат:
- плохие входные данные не валят приложение и не ломают session flow.

### 7. Prepare Minimal Security Baseline

Статус:
- `medium priority`

Задачи:
- проверить consistency для `NodeIdentity` и fingerprint;
- усилить handshake verification;
- зафиксировать replay protection assumptions;
- коротко описать trust model и ограничения безопасности для demo.

Результат:
- у команды есть внятное security explanation для жюри.

## Best Execution Order

1. `Reconnect/sync`
2. `Ack/retry/timeout`
3. `Dedup + loop protection`
4. `Critical tests`
5. `Protocol freeze notes`
6. `Failure handling`
7. `Security baseline`

## Definition Of Done For Person 1 Next Stage

Следующий этап `PERSON_1` можно считать закрытым, если:
- reconnect/sync реально работает;
- retry rules формализованы и ограничены;
- dedup и loop protection устойчивы;
- critical tests проходят;
- protocol rules явно зафиксированы;
- invalid packets не валят приложение;
- security baseline объясним в demo.

## Suggested Commit Topics

- `feat: implement reconnect inventory sync`
- `feat: harden retry timeout rules`
- `feat: finalize relay dedup and loop protection`
- `test: cover protocol reconnect and retry cases`
- `docs: freeze protocol invariants`
- `feat: add packet validation and failure handling`

