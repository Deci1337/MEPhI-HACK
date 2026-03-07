# PERSON_2 — UI / Transport / Reliability — Sprint 2

Граница: `MainPage.xaml`, `MainPage.xaml.cs`, `MauiProgram.cs`. Core не трогать.
Координация с PERSON_1 только по новым event-ам (`RetryExhausted`).

---

## Задача 1 — Показывать статус доставки прямо в чате (CRITICAL)

**Файл:** `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

**Проблема:** `OnDeliveryStatusChanged` только пишет в TechLog. Пользователь не видит
статус `Sent / Delivered / Failed` рядом с сообщением.

**Что сделать:**

1. Добавить `Status` в `ChatItem.cs`:
```csharp
public string? Status { get; set; }  // "Sent", "Delivered", "Failed", null
public bool HasStatus => Status != null;
```

2. При отправке сообщения в `OnSendClicked` — обновить статус `ChatItem` по `MessageId`:
```csharp
// Хранить маппинг: messageId -> ChatItem
private readonly Dictionary<string, ChatItem> _pendingItems = new();
```

3. В `OnDeliveryStatusChanged` — найти `ChatItem` по `messageId` и обновить:
```csharp
if (_pendingItems.TryGetValue(messageId, out var item))
{
    item.Status = status.ToString();
    // ChatItem должен реализовывать INotifyPropertyChanged для обновления UI
}
```

4. В XAML DataTemplate добавить индикатор статуса рядом с текстом:
```xml
<Label Text="{Binding Status}" FontSize="9"
       TextColor="{StaticResource SeaShellMuted}"
       IsVisible="{Binding HasStatus}"
       HorizontalOptions="End" />
```

**Важно:** `ChatItem` нужно сделать `INotifyPropertyChanged` (или использовать
`ObservableObject` из CommunityToolkit).

---

## Задача 2 — Diagnostics Panel (расширить TechLog)

**Файлы:**
- `MassangerMaximka/MassangerMaximka/MainPage.xaml`
- `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

**Проблема:** метрики `RTT / PacketLoss / Throughput / Retries` пишутся в TechLog как текст.
Жюри не видит их явно. Нужна компактная панель над TechLog.

**Что добавить в XAML** — в `Grid Row="5"` (секция Technical Logs) перед `ScrollView`:

```xml
<!-- Metrics bar -->
<Grid Grid.Row="5" RowDefinitions="Auto,Auto,*">
    <BoxView Grid.Row="0" HeightRequest="1" Color="{StaticResource CoalBorder}" />
    <Grid Grid.Row="1" Padding="10,5" ColumnDefinitions="*,*,*,*" ColumnSpacing="8"
          BackgroundColor="{StaticResource CoalPanel}">
        <StackLayout Grid.Column="0" Spacing="1">
            <Label Text="RTT" FontSize="8" TextColor="{StaticResource SeaShellMuted}" />
            <Label x:Name="MetricRttLabel" Text="--" FontSize="11"
                   TextColor="{StaticResource SeaShell}" FontAttributes="Bold" />
        </StackLayout>
        <StackLayout Grid.Column="1" Spacing="1">
            <Label Text="LOSS" FontSize="8" TextColor="{StaticResource SeaShellMuted}" />
            <Label x:Name="MetricLossLabel" Text="--" FontSize="11"
                   TextColor="{StaticResource SeaShell}" FontAttributes="Bold" />
        </StackLayout>
        <StackLayout Grid.Column="2" Spacing="1">
            <Label Text="QUALITY" FontSize="8" TextColor="{StaticResource SeaShellMuted}" />
            <Label x:Name="MetricQualityLabel" Text="--" FontSize="11"
                   TextColor="{StaticResource SeaShell}" FontAttributes="Bold" />
        </StackLayout>
        <StackLayout Grid.Column="3" Spacing="1">
            <Label Text="RETRIES" FontSize="8" TextColor="{StaticResource SeaShellMuted}" />
            <Label x:Name="MetricRetriesLabel" Text="--" FontSize="11"
                   TextColor="{StaticResource SeaShell}" FontAttributes="Bold" />
        </StackLayout>
    </Grid>
    <ScrollView Grid.Row="2" x:Name="TechLogScrollView" Padding="10,6">
        <VerticalStackLayout x:Name="TechLogStack" Spacing="1" />
    </ScrollView>
</Grid>
```

Обновить `RowDefinitions` для Grid Row="5": `"Auto,Auto,*"` вместо `"Auto,*"`.

**В MainPage.xaml.cs** — в `OnMetricsUpdated`:
```csharp
private void OnMetricsUpdated(ConnectionMetrics metrics)
{
    MainThread.BeginInvokeOnMainThread(() =>
    {
        MetricRttLabel.Text = $"{metrics.RttMs:F0}ms";
        MetricLossLabel.Text = $"{metrics.PacketLossPercent:F1}%";
        MetricRetriesLabel.Text = metrics.RetryCount.ToString();
        var quality = _metrics?.GetQuality(metrics.PeerNodeId) ?? ConnectionQuality.Unknown;
        MetricQualityLabel.Text = quality.ToString();
        MetricQualityLabel.TextColor = quality switch
        {
            ConnectionQuality.Excellent => Color.FromArgb("#4CAF50"),
            ConnectionQuality.Good      => Color.FromArgb("#8BC34A"),
            ConnectionQuality.Fair      => Color.FromArgb("#FFC107"),
            ConnectionQuality.Poor      => Color.FromArgb("#F44336"),
            _                           => Color.FromArgb("#7A7570")
        };
    });
}
```

---

## Задача 3 — Показать статус файловой передачи (integrity)

**Файл:** `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

**Проблема:** `OnFileReceived` отображает имя файла, но не показывает результат проверки
целостности (SHA256 hash).

**Что сделать:**

В `OnFileReceived` — добавить отображение хэша:
```csharp
// Если FileTransferService возвращает hash через событие (нужно проверить сигнатуру),
// показать:
AppendChat($"[File] {fileName} — integrity OK (SHA256 verified)");

// Если hash недоступен через событие, добавить сигнатуру event в FileTransferService:
// event Action<string, string, bool>? FileReceived; // transferId, path, integrityOk
```

Проверить сигнатуру `FileTransferService.FileReceived` и при необходимости скоординироваться
с PERSON_1 для добавления `bool integrityOk` в event.

---

## Задача 4 — Показывать relay-статус в чате

**Файл:** `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

**Проблема:** `TransportChatMessage` имеет поле для определения relay (hop > 0), но в чате
нет индикатора что сообщение пришло через relay.

**Что сделать в `OnMessageReceived`:**
```csharp
// Если сообщение пришло через relay, добавить маркер
var isRelayed = /* определить из hop count или добавить поле в TransportChatMessage */;
var prefix = isRelayed ? "[relay] " : "";
AppendChat($"{prefix}{msg.FromNodeId}: {text}");
```

Проверить `TransportChatMessage` — если нет поля `IsRelayed` или `HopCount`, добавить его
через `TransportEnvelope.HopCount` при получении в `TcpChatTransport`.

---

## Задача 5 — README.md

**Файл:** `README.md` (в корне репозитория)

Создать `README.md` с разделами:

```
# HexTeam Messenger

Офлайн-децентрализованный P2P мессенджер для Windows и Android.
Работает без интернета в локальной сети или hotspot.

## Возможности
- Peer discovery через UDP broadcast
- P2P текстовый чат
- Relay forwarding (A -> B -> C через промежуточный узел)
- Передача файлов с проверкой целостности (SHA256)
- Голосовые звонки и голосовые сообщения
- Метрики: RTT, packet loss, throughput

## Сборка
- .NET 8 SDK
- MAUI workload: dotnet workload install maui
- dotnet build MassangerMaximka/MassangerMaximka/

## Запуск
### Windows
dotnet run --project MassangerMaximka/MassangerMaximka/

### Android
dotnet build -t:Run -f net8.0-android

### Несколько экземпляров (разные порты)
set HEX_TCP_PORT=45681
dotnet run --project MassangerMaximka/MassangerMaximka/

## Тестирование на 3 устройствах
1. Все устройства в одной Wi-Fi сети (или Android hotspot)
2. Запустить на каждом устройстве
3. Через 5-10 сек Discovery покажет соседей в списке Peers
4. Если автодискавери не сработало — ввести IP:port вручную

## Метрики
Видны в нижней панели приложения (RTT, LOSS, QUALITY, RETRIES)
Подробные логи — в секции Technical Logs
```

---

## Задача 6 — DEMO_SCRIPT.md

**Файл:** `DEMO_SCRIPT.md` (в корне репозитория)

```
# Demo Script — HexTeam Messenger

## Устройства
- Device A: Windows (запущено на порту 45680)
- Device B: Android (запущено на порту 45680)
- Device C: Windows (запущено на порту 45681)

## Сценарий 1: Discovery + Direct Chat
1. Запустить A и B в одной сети
2. Показать что B появился в списке Peers у A (UDP discovery)
3. Отправить сообщение A -> B, показать доставку
4. Отправить B -> A, показать двустороннюю связь

## Сценарий 2: Relay A -> B -> C
1. Запустить все три устройства
2. A и C не подключены напрямую (или показать через IP)
3. Сообщение от A к C идёт через B как relay
4. Показать в TechLog: hop=1 у C

## Сценарий 3: File Transfer
1. Выбрать файл на A (кнопка File)
2. Показать прогресс в FileTransferLabel
3. После завершения — integrity OK в чате

## Сценарий 4: Voice Call
1. Нажать Call на A, принять на B
2. Показать голосовую связь в реальном времени
3. Завершить звонок

## Сценарий 5: Voice Messages
1. Нажать REC, сказать слово, нажать STOP
2. Голосовое появится в чате у B с кнопкой Play

## Fallback если Discovery не работает
- Ввести IP:port вручную в поле IP:port -> Connect

## Метрики для жюри
- Показать секцию RTT / LOSS / QUALITY
- Объяснить что это измеряется через ping/pong каждые 3 секунды
```

---

## Порядок выполнения

1. Задача 5 (README) — не зависит ни от чего, можно начать сразу
2. Задача 2 (Diagnostics panel) — независима
3. Задача 1 (Delivery status) — зависит от `INotifyPropertyChanged` в ChatItem
4. Задача 4 (Relay marker) — зависит от `TransportChatMessage`
5. Задача 3 (File integrity) — зависит от `FileTransferService` event signature
6. Задача 6 (DEMO_SCRIPT) — в конце

## Координация с PERSON_1

- Если PERSON_1 добавит `RetryExhausted` event в `RetryPolicy` — подключить в UI:
  показывать `Failed` у ChatItem когда все retry исчерпаны.
- Если PERSON_1 изменит `FileTransferService.FileReceived` сигнатуру (добавит `integrityOk`)
  — обновить `OnFileReceived` в MainPage.

## Файлы которые нельзя трогать

- Всё в `HexTeam.Messenger.Core/` (кроме согласованных изменений)
- `VoiceCallManager.cs`
- `ChatItem.cs` — только если добавляешь `Status` с `INotifyPropertyChanged`
