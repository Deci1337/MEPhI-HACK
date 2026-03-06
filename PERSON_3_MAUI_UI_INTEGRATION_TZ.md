# Person 3 Technical Assignment

## Role

`MAUI UI + Integration + Demo + Docs`

Ты отвечаешь за то, чтобы проект выглядел как цельное приложение, собирался на `Windows + Android`, был понятен жюри и демонстрировался без хаоса.

## Project Context

Команда строит офлайн-децентрализованный мессенджер на `.NET MAUI C#`.

Проект должен:
- работать без интернета;
- находить peers в локальной сети;
- открывать P2P-сессии;
- поддерживать текстовый чат;
- пересылать сообщения и файлы через relay;
- показывать file transfer status;
- по возможности показывать `voice`;
- иметь diagnostics and metrics;
- быть пригодным для защиты на хакатоне.

Условия:
- `1 Android`
- `2 Windows`
- `48 часов`

Поэтому твоя задача: сделать **понятный, стабильный и презентабельный клиент**, а не перегруженный интерфейс.

## Main Objective

Ты должен собрать всё в единое приложение:
- подключить core и transport слои;
- сделать UI для peers/chat/files/diagnostics;
- обеспечить кроссплатформенную сборку;
- сделать README и архитектурную схему;
- собрать demo script.

## What Exactly You Own

Ты владеешь:
- MAUI pages
- viewmodels
- app navigation
- app-wide DI
- `MauiProgram.cs`
- `AppShell.xaml`
- общий wiring services
- Android/Windows integration
- permissions
- diagnostics UI
- README
- architecture docs
- threat model
- demo script

Ты **не** владеешь:
- protocol logic as business rules
- retry algorithm internals
- relay routing rules
- transport internals

## You Are The Integration Owner

Это значит:
- только ты должен трогать integration files;
- только ты сводишь вместе ветки Person 1 и Person 2;
- ты отвечаешь, чтобы app не развалилась при merge.

## Required Project Area

Основной проект:
- `HexTeam.Messenger.App`

Ты также затрагиваешь:
- solution wiring
- docs at project root

## UI Scope

Ты должен сделать минимум следующие экраны:
- peer list
- chat screen
- file transfer screen
- diagnostics screen
- settings / connection screen

### Peer List Screen

Должна показывать:
- найденные peers
- статус peers
- возможность ручного подключения
- relay-capable peer information if available

### Chat Screen

Должна показывать:
- историю сообщений
- input box
- send action
- delivery status
- connection status

### File Transfer Screen

Должна показывать:
- выбор файла
- progress
- sending / receiving / paused / resumed / failed / completed
- integrity result

### Diagnostics Screen

Должна показывать:
- connected / disconnected / relayed / degraded
- RTT
- retries
- packet loss
- throughput
- relay state
- voice quality metrics if available

## Documentation Scope

Ты отвечаешь за документы, которые влияют на баллы:
- `README.md`
- `ARCHITECTURE.md`
- `THREAT_MODEL.md`
- `DEMO_SCRIPT.md`

### README Must Include

- что это за проект;
- как собрать;
- как запустить;
- как поднять локальную сеть / hotspot;
- как протестировать на трёх устройствах;
- ограничения текущей версии;
- где смотреть метрики.

### ARCHITECTURE Must Include

- solution structure
- components
- transport choices
- relay topology
- protocol overview
- reconnect logic overview

### THREAT_MODEL Must Include

- spoofing
- replay
- relay overload
- spam/flood
- tampering assumptions

### DEMO_SCRIPT Must Include

- порядок действий на защите;
- кто с какого устройства что показывает;
- fallback plan если discovery лагает;
- что делать если voice временно отключён.

## Inputs From Other People

Ты получаешь:
- от Person 1: protocol contracts, status enums, reliability model
- от Person 2: discovery/connect/file/voice/metrics services

## Outputs For Other People

Ты должен отдать:
- UI integration points
- screen requirements
- expected status models
- final integrated build
- final demo flow

## Suggested Files

Примерно:
- `App.xaml`
- `AppShell.xaml`
- `MauiProgram.cs`
- `Pages/PeersPage.xaml`
- `Pages/ChatPage.xaml`
- `Pages/TransferPage.xaml`
- `Pages/DiagnosticsPage.xaml`
- `Pages/SettingsPage.xaml`
- `ViewModels/MainViewModel.cs`
- `ViewModels/PeersViewModel.cs`
- `ViewModels/ChatViewModel.cs`
- `ViewModels/TransferViewModel.cs`
- `ViewModels/DiagnosticsViewModel.cs`

## UX Requirements

UI должен быть не сложным, а ясным.

Нужно показать:
- найденные устройства;
- соединён ли peer;
- идёт ли relay;
- отправлено ли сообщение;
- доставлено ли сообщение;
- идёт ли передача файла;
- хорошее ли качество соединения.

Важно:
- judge должен понимать состояние системы без объяснения исходников.

## Integration Rules

Ты не должен:
- менять transport contracts на эмоциях;
- ломать protocol model ради UI convenience;
- переписывать core behavior inside UI;
- пихать бизнес-логику в pages.

Нужно:
- работать через services/viewmodels;
- держать UI максимально thin;
- давать понятные states и ошибки.

## Manual Scenarios You Must Own

Ты отвечаешь за то, чтобы эти сценарии можно было показать:
- peer discovery
- manual connect fallback
- direct chat
- relay chat
- file transfer
- interrupted connection and reconnect
- diagnostics and metrics
- voice demo if ready

## Milestones For You

### Hours 0-2

- создать MAUI pages skeleton
- создать navigation
- подготовить базовые viewmodels

### Hours 2-8

- peer list UI
- chat UI
- basic state bindings

### Hours 8-16

- transfer UI
- connection state UI
- relay state UI

### Hours 16-28

- diagnostics screen
- integration with Android/Windows specifics
- first smoke test on real devices

### Hours 28-40

- README
- architecture diagram
- threat model
- demo script

### Hours 40-48

- stabilization only
- final merge coordination
- demo rehearsals

## Done Criteria

Твой блок готов, если:
- приложение выглядит как цельный продукт;
- peer list понятен;
- chat screen понятен;
- file status понятен;
- diagnostics screen полезен;
- приложение собирается и запускается на `Windows + Android`;
- docs готовы;
- есть repeatable demo flow.

## What You Must Not Do

Не трать время на:
- сверхсложный дизайн;
- анимации ради красоты;
- большие визуальные рефакторинги;
- перенос логики transport в UI;
- video UI;
- group chat UI;
- лишние настройки, не влияющие на demo.

## Branch

- `feature/maui-ui-demo`

## Commit Examples

- `feat: add peers and chat pages`
- `feat: add delivery and connection status ui`
- `feat: add diagnostics screen`
- `docs: add architecture and threat model`
- `docs: add final demo script`

## Priority Reminder

Твоя задача:
- сделать приложение понятным;
- собрать всё вместе;
- подготовить сильную защиту;
- не дать проекту развалиться на финальном merge.

