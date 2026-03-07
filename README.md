# HexTeam Messenger

Офлайн-децентрализованный P2P мессенджер для Windows и Android.
Работает без интернета — в локальной сети или через мобильный hotspot.

## Возможности

- Peer discovery через UDP broadcast (автоматически)
- Ручное подключение по IP:port (fallback)
- P2P текстовый чат с подтверждением доставки (Ack)
- Relay forwarding: A → B → C через промежуточный узел
- Передача файлов по чанкам с проверкой целостности (SHA256)
- Голосовые звонки в реальном времени (UDP, 200ms чанки)
- Голосовые сообщения (запись → отправка → воспроизведение в чате)
- Метрики качества соединения: RTT, Packet Loss, Quality, Retries
- История подключённых пиров (авто-переподключение при следующем запуске)

## Технологии

- .NET 9 MAUI (Windows + Android из одного codebase)
- TCP для чата и файлов
- UDP для discovery и голоса
- AES-256-GCM шифрование трафика
- ECDH ключевой обмен
- SHA256 для целостности файлов

## Требования

- .NET 9 SDK
- MAUI workload: `dotnet workload install maui`

## Сборка

```bash
dotnet build MassangerMaximka/MassangerMaximka/
```

## Запуск

### Windows

```bash
dotnet run --project MassangerMaximka/MassangerMaximka/
```

### Windows — второй экземпляр (другой порт)

```bash
set HEX_TCP_PORT=45681
dotnet run --project MassangerMaximka/MassangerMaximka/
```

### Android

Собрать target `net9.0-android` и деплоить на устройство или эмулятор.

**Если сборка падает с "Permission denied" (проект в Desktop/OneDrive):**

```powershell
.\build-android.ps1
```

Скрипт копирует проект в `C:\Temp\mifi-hack` и собирает там. APK будет в `C:\Temp\mifi-hack\MassangerMaximka\MassangerMaximka\bin\Debug\net9.0-android\`. Установить можно через `adb install` или скопировать вручную.

## Тестирование на 3 устройствах

1. Все устройства — в одной Wi-Fi сети или подключены к Android hotspot
2. Запустить приложение на каждом
3. Через 5–10 секунд discovery покажет соседей в списке Peers (слева)
4. Если autodiscovery не сработало — ввести `IP:port` вручную в поле внизу панели и нажать Connect

## Структура

```
MassangerMaximka/
  MassangerMaximka/         — MAUI UI (Windows + Android)
  HexTeam.Messenger.Core/   — протокол, транспорт, сервисы
  HexTeam.Messenger.Tests/  — unit тесты протокола
```

## Порты (по умолчанию)

| Назначение | Порт |
|-----------|------|
| TCP чат + файлы | 45680 |
| UDP discovery | 45678 |
| UDP голос | 45780 |

Можно переопределить через переменную окружения `HEX_TCP_PORT`.

## Метрики

Метрики обновляются каждые 3 секунды (ping/pong) и отображаются в панели над техническими логами:

| Метрика | Описание |
|---------|---------|
| RTT | Round-trip time в мс |
| LOSS | Процент потерянных пакетов |
| QUALITY | Excellent / Good / Fair / Poor |
| RETRIES | Количество повторных отправок |

Подробные логи — в секции Technical Logs внизу приложения.
