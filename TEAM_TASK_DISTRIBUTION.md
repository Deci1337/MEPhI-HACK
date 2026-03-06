# Team Task Distribution

## Project Goal

Собрать офлайн-мессенджер на `.NET MAUI C#` для `Windows + Android`, который:
- работает без интернета в локальной сети / hotspot;
- поддерживает `peer discovery`;
- открывает `P2P session`;
- отправляет текстовые сообщения;
- умеет пересылать сообщения и файлы через relay-узел;
- поддерживает передачу файлов с контролем целостности;
- по возможности показывает `voice` в real-time;
- даёт максимум баллов по ТЗ.

## Team Structure

Команда из 3 человек делится по стабильным техническим границам:
- **Person 1**: `Core + Protocol + Reliability`
- **Person 2**: `Network + Transport + File + Voice`
- **Person 3**: `MAUI UI + Integration + Demo + Docs`

Такое разделение уменьшает конфликты в коде и позволяет работать параллельно.

## Shared Solution Structure

После создания MAUI-приложения используйте такую структуру:
- `HexTeam.Messenger.App` — `MAUI UI`, Windows + Android
- `HexTeam.Messenger.Core` — модели, протокол, бизнес-логика
- `HexTeam.Messenger.Tests` — unit/integration tests

## Milestones

- `[ ]` Milestone 0: создать solution и базовую структуру проектов
- `[ ]` Milestone 1: заморозить контракты протокола и relay-правила
- `[ ]` Milestone 2: поднять `discovery + P2P + text chat`
- `[ ]` Milestone 3: добавить `relay/multihop + Ack/retry/dedup`
- `[ ]` Milestone 4: реализовать `file transfer`
- `[ ]` Milestone 5: реализовать `voice + metrics`
- `[ ]` Milestone 6: добить `security + docs + demo`

## Person 1: Core + Protocol + Reliability

### Main Responsibility

Этот человек отвечает за саму логику системы и закрывает самые важные требования ТЗ по:
- архитектуре сети;
- дедупликации;
- защите от петель;
- `Ack/retry`;
- работе при разрывах;
- relay-логике.

### Owns

- packet/message contracts
- `NodeId`, `PeerInfo`, `SessionId`, `MessageId`, `PacketId`
- `Envelope`
- `Ack`, timeout, retry
- deduplication
- loop protection
- `HopCount`, `MaxHops`
- reconnect/sync logic
- хранение `seen packets`
- unit tests на поведение протокола

### Tasks

1. Описать формат всех пакетов и envelope.
2. Зафиксировать relay-правила.
3. Реализовать `Ack + retry + timeout`.
4. Реализовать deduplication.
5. Реализовать защиту от петель.
6. Реализовать sync после reconnect.
7. Написать тесты на critical behavior.

### Deliverables

- `Envelope`
- `HelloPacket`
- `ChatPacket`
- `AckPacket`
- `InventoryPacket`
- `MissingRequestPacket`
- `FileChunkPacket`
- `VoiceFramePacket`
- `RelayService`
- `RetryPolicy`
- `SeenPacketStore`

### Required Tests

- duplicate packet ignored
- packet is not forwarded twice
- packet is dropped when `HopCount >= MaxHops`
- retry starts after timeout
- reconnect requests missing messages
- relay does not send packet back to previous peer

### Branch

- `feature/core-protocol`

## Person 2: Network + Transport + File + Voice

### Main Responsibility

Этот человек отвечает за реальную передачу данных между устройствами.

### Owns

- `UDP discovery`
- manual connect по `IP:port`
- `TCP` transport для chat/files
- `UDP` transport для voice
- relay forwarding на уровне транспорта
- file chunk protocol
- resume передачи
- hash/integrity
- latency/loss/jitter metrics

### Tasks

1. Поднять `UDP broadcast discovery`.
2. Сделать fallback через ручной `IP:port`.
3. Реализовать `TCP listener/client`.
4. Передавать `ChatPacket` между узлами.
5. Подключить relay forwarding.
6. Реализовать file transfer по чанкам.
7. Добавить hash/integrity check.
8. Реализовать resume с последнего подтверждённого chunk.
9. Поднять минимальный `voice` через `UDP`.
10. Собирать метрики для diagnostics screen.

### Deliverables

- `DiscoveryService`
- `PeerConnectionService`
- `TcpChatTransport`
- `RelayForwarder`
- `FileTransferService`
- `ChunkAssembler`
- `VoiceSessionService`
- `MetricsService`

### Critical Features From TOR

- список найденных устройств
- P2P session
- relay `A -> B -> C`
- file transfer with progress and integrity
- real-time voice with measurements

### Branch

- `feature/transport-file-voice`

## Person 3: MAUI UI + Integration + Demo + Docs

### Main Responsibility

Этот человек отвечает за продуктовую часть, интеграцию всех сервисов и готовность к защите.

### Owns

- `MAUI` UI
- pages/viewmodels
- Android/Windows integration
- permissions
- diagnostics screen
- app-wide DI wiring
- README
- architecture diagram
- threat model
- presentation/demo script

### Tasks

1. Создать страницы приложения.
2. Сделать peer list screen.
3. Сделать chat screen.
4. Сделать file transfer screen.
5. Сделать diagnostics screen.
6. Показать connection state, relay state, delivery state, quality state.
7. Интегрировать `Core` и transport services.
8. Подготовить README и архитектурную схему.
9. Подготовить demo script.
10. Провести smoke test на `Windows + Android`.

### Deliverables

- `PeersPage`
- `ChatPage`
- `TransferPage`
- `DiagnosticsPage`
- `SettingsPage`
- `MainViewModel`
- `ChatViewModel`
- `TransferViewModel`
- `DiagnosticsViewModel`
- `README.md`
- `ARCHITECTURE.md`
- `THREAT_MODEL.md`
- `DEMO_SCRIPT.md`

### Branch

- `feature/maui-ui-demo`

## Shared Files Ownership

Чтобы не было merge hell, только один человек должен менять:
- `MauiProgram.cs`
- `AppShell.xaml`
- app-wide DI registration
- общие ресурсы, темы и стили
- финальное wiring всех сервисов

Рекомендуемый владелец этих файлов:
- **Person 3**

## First 48 Hours Timeline

### Hours 0-2

- Person 1: фиксирует DTO и packet types
- Person 2: делает discovery prototype
- Person 3: создаёт MAUI navigation и базовые страницы

### Hours 2-8

- Person 1: envelope, ack, retry, dedup
- Person 2: tcp transport + peer connect
- Person 3: peer list + chat UI + bindings

### Hours 8-16

- Person 1: relay logic + reconnect sync
- Person 2: multihop forwarding + file chunks
- Person 3: connection states + transfer UI + logs

### Hours 16-28

- Person 1: tests + failure handling
- Person 2: hash, resume, throttling, voice prototype
- Person 3: diagnostics screen + Android/Windows integration

### Hours 28-40

- Person 1: security baseline
- Person 2: latency/loss/jitter metrics
- Person 3: README, scheme, threat model, demo flow

### Hours 40-48

- все занимаются только стабилизацией
- никаких новых больших фич
- только bugfix, tests, polish, final demo rehearsal

## Git Workflow

### Branches

- `main`
- `feature/core-protocol`
- `feature/transport-file-voice`
- `feature/maui-ui-demo`

### Rules

- Мержиться каждые `3-4 часа`.
- Каждый коммит должен быть маленьким и законченным.
- Перед merge:
  - подтянуть свежие изменения;
  - проверить сборку;
  - сделать короткий smoke test;
  - убедиться, что общие DTO не сломаны.

### Commit Style

- `feat: add udp discovery broadcast`
- `feat: implement relay ttl and dedup`
- `feat: add chunked file transfer`
- `fix: resume file transfer after disconnect`
- `docs: add threat model and demo steps`

## Freeze Before Major Coding

Перед активной разработкой нужно заморозить:
- формат `Envelope`
- список packet types
- формат `NodeId`
- `Ack timeout`
- `MaxHops`
- file chunk size
- hash algorithm
- список connection states в UI
- кто владеет integration files

## Definition Of Done

Проект готов к защите, если:
- 3 устройства видят друг друга в peer list;
- direct chat работает;
- relay chat работает через промежуточный узел;
- file transfer показывает progress/status;
- integrity check проходит;
- reconnect/sync работает;
- нет дублей сообщений после relay/retry;
- есть хотя бы минимальная `voice` demo или очень сильное архитектурное описание и метрики;
- diagnostics screen показывает качество соединения;
- есть README, архитектурная схема, threat model, logs/metrics;
- demo повторяется минимум 2 раза подряд без ручной магии.

## What To Avoid In First 24 Hours

Не трогать в первые сутки:
- video
- BLE
- Wi-Fi Direct
- NAT traversal
- group chat
- сложную mesh-маршрутизацию
- production-grade E2EE

## Recommended Skills To Use

- `Skills/skills/brainstorming/SKILL.md`
- `Skills/skills/writing-plans/SKILL.md`
- `Skills/skills/plan-writing/SKILL.md`
- `Skills/skills/software-architecture/SKILL.md`
- `Skills/skills/dotnet-architect/SKILL.md`
- `Skills/skills/csharp-pro/SKILL.md`
- `Skills/skills/architect-review/SKILL.md`
- `Skills/skills/testing-qa/SKILL.md`
- `Skills/skills/requesting-code-review/SKILL.md`
- `Skills/skills/verification-before-completion/SKILL.md`

## Final Advice

- Сначала берите полную базовую часть ТЗ.
- Потом добирайте бонусы за `cross-platform`, `UI/UX`, `tests`.
- Не пытайтесь выиграть за счёт количества фич.
- Победный проект здесь — это не самый большой, а самый стабильный, объяснимый и демонстрируемый.
