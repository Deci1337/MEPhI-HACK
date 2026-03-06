# Person 2 Technical Assignment

## Role

`Network + Transport + File + Voice`

Ты отвечаешь за реальную передачу данных между устройствами: discovery, соединения, transport, file transfer, voice и метрики качества.

## Project Context

Команда строит офлайн-децентрализованный мессенджер на `.NET MAUI C#` для:
- `Windows`
- `Android`

Приложение должно:
- работать без интернета в локальной сети;
- находить peers;
- открывать P2P-сессию;
- передавать текст;
- пересылать сообщения и файлы через relay;
- передавать файлы с прогрессом и контролем целостности;
- по возможности поддерживать `voice`;
- собирать метрики качества соединения.

Ограничения:
- `1 Android`
- `2 Windows`
- `48 часов`

Поэтому тебе нужен не идеальный transport, а **простой, надёжный и быстро демонстрируемый**.

## Main Objective

Ты должен дать рабочий transport stack, который обеспечивает:
- discovery peers
- direct P2P session
- relay delivery
- file transfer
- voice prototype
- diagnostics metrics

Это один из самых зрелищных блоков проекта и напрямую влияет на demo.

## What Exactly You Own

Твоя зона:
- `UDP discovery`
- manual connect by `IP:port`
- `TCP` transport для text/file
- `UDP` transport для voice
- relay forwarding на transport side
- file transfer sessions
- chunk send/receive
- resume logic
- integrity check
- metrics collection

Ты **не** владеешь:
- UI pages
- app-wide navigation
- protocol contracts after freeze
- README and presentation

## Required Project Area

Основной код:
- `HexTeam.Messenger.Core`
- transport implementation может частично жить в `App`, если зависит от platform runtime

Тесты и harness:
- `HexTeam.Messenger.Tests`

## Transport Decisions You Must Respect

### Discovery

Основной путь:
- `UDP broadcast`

Fallback:
- ручной ввод `IP:port`

### Text And File

Использовать:
- `TCP`

### Voice

Использовать:
- `UDP`

Не менять этот трек без очень серьёзной причины.

## What You Must Build

### 1. Peer Discovery

Нужно:
- отправлять broadcast beacon;
- принимать beacons;
- обновлять peer list;
- уметь работать с ручным fallback.

Важно:
- discovery не должен быть единственной точкой успеха;
- manual connect must save the demo if auto-discovery glitches.

### 2. P2P Session Establishment

Нужно:
- открыть соединение между двумя peers;
- корректно обрабатывать success/failure;
- отдавать понятный статус в UI.

### 3. Chat Delivery Transport

Нужно:
- передавать chat packets по `TCP`;
- уметь принимать и отправлять данные;
- передавать данные в core/reliability layer;
- не ломать contracts, которые определит Person 1.

### 4. Relay Delivery

Нужно:
- передавать пакеты дальше на другой peer;
- обеспечивать working path `A -> B -> C`;
- уметь работать через Android relay node.

### 5. File Transfer

Нужно:
- разбивать файл на чанки;
- отправлять чанки;
- принимать chunk ack;
- поддерживать progress;
- поддерживать resume;
- проверять integrity;
- не перегружать канал.

### 6. Voice

Нужно:
- поднять минимальный voice prototype через `UDP`;
- делать голос приоритетнее видео;
- показывать хотя бы базовые real-time measurements.

### 7. Metrics

Нужно собирать:
- connection RTT
- retries
- throughput
- file progress
- packet loss estimate
- jitter / buffer info for voice
- reconnect attempts

## Suggested Files

Примерно:
- `Discovery/UdpDiscoveryService.cs`
- `Transport/PeerConnectionService.cs`
- `Transport/TcpChatTransport.cs`
- `Transport/RelayForwarder.cs`
- `Transport/UdpVoiceTransport.cs`
- `FileTransfer/FileTransferService.cs`
- `FileTransfer/FileChunkSender.cs`
- `FileTransfer/FileChunkReceiver.cs`
- `FileTransfer/FileIntegrityService.cs`
- `Metrics/MetricsService.cs`
- `Metrics/ConnectionQualityCalculator.cs`

## Inputs From Other People

Ты получаешь:
- от Person 1: frozen DTO/contracts and relay rules
- от Person 3: UI integration points and screen requirements

## Outputs For Other People

Ты должен отдать:
- рабочий discovery service
- connect/disconnect APIs
- send/receive callbacks
- file transfer progress model
- metrics data model
- voice session APIs

## File Transfer Requirements

Ты обязан реализовать:
- chunk-based transfer
- progress status
- integrity validation
- resume from last confirmed chunk
- partial transfer handling
- bandwidth throttling or reasonable load limiting

Желательно:
- не забивать канал так, чтобы chat стал неотзывчивым

## Voice Requirements

Минимальный MVP:
- voice start
- voice frame send
- voice frame receive
- voice stop
- базовый jitter buffer
- хотя бы rough metrics

Если voice не успевает стабилизироваться:
- оставить architecture and measurements ready
- не ломать остальной demo из-за voice

## Manual Test Cases You Must Run

Обязательные ручные проверки:
- `Windows A <-> Android`
- `Android <-> Windows C`
- `Windows A -> Android -> Windows C`
- direct chat
- relay chat
- file transfer
- interrupted file transfer and resume
- reconnect after temporary disconnect
- voice between two devices if ready

## Milestones For You

### Hours 0-2

- прототип discovery
- согласование connect flow

### Hours 2-8

- direct `TCP` connection
- direct text transport

### Hours 8-16

- relay transport path
- stable receive/send callbacks
- first end-to-end relay path

### Hours 16-28

- file transfer
- chunk ack
- resume
- integrity

### Hours 28-40

- voice prototype
- metrics collection
- connection quality model

### Hours 40-48

- stabilization only
- no risky transport rewrites

## Done Criteria

Твой блок готов, если:
- peers находятся в LAN;
- manual connect работает;
- direct connect работает;
- relay `A -> B -> C` реально работает;
- file transfer идёт и показывает progress;
- integrity check проходит;
- resume работает;
- есть metrics для diagnostics screen;
- voice либо работает минимально, либо аккуратно изолирован и не ломает main demo.

## What You Must Not Do

Не трать время на:
- video
- BLE
- Wi-Fi Direct
- NAT traversal
- custom exotic transport
- переделку protocol contracts без синхронизации
- UI polish

## Branch

- `feature/transport-file-voice`

## Commit Examples

- `feat: add udp peer discovery`
- `feat: add manual peer connect fallback`
- `feat: implement tcp chat transport`
- `feat: add relay forwarding path`
- `feat: implement chunked file transfer`
- `feat: add file resume and integrity validation`
- `feat: add udp voice prototype and metrics`

## Priority Reminder

Главная цель твоего блока:
- не максимальная технологичность,
- а working demo на трёх устройствах.

