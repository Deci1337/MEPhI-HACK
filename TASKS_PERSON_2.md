# PERSON_2 — UI / Голосовые сообщения в чате

Все задачи в UI-слое. Не трогаем VoiceCallManager.cs и Core-проект.

---

## Задача 1 — Голосовые сообщения в чате с кнопкой Play (CRITICAL)

**Файлы:**
- `MassangerMaximka/MassangerMaximka/MainPage.xaml`
- `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

**Симптом:** входящее голосовое сообщение автовоспроизводится (`PlayVoiceFile(savedPath)`) и не отображается в чате как элемент. Глобальная кнопка PLAY проигрывает только последнее полученное сообщение.

**Цель:** каждое голосовое сообщение — отдельная строка в чате с кнопкой Play рядом.

---

### Шаг 1.1 — Убрать автовоспроизведение при получении

В `MainPage.xaml.cs`, метод `OnFileReceived` (~строка 259):

```csharp
// БЫЛО
AppendChat($"[Voice Message] {fileName} -- press Play");
PlayVoiceFile(savedPath);

// СТАЛО
AppendVoiceMessage(savedPath, fromPeer: true);
```

---

### Шаг 1.2 — Добавить модель чат-сообщения

Добавить в `MainPage.xaml.cs` (или отдельный файл `ChatItem.cs`):

```csharp
public sealed class ChatItem
{
    public string Text { get; init; } = "";
    public string? VoicePath { get; init; }   // null = текстовое сообщение
    public bool IsVoice => VoicePath != null;
}
```

Заменить `_chatLog` (текущий `List<string>`) на `ObservableCollection<ChatItem>`:

```csharp
// БЫЛО
private readonly List<string> _chatLog = new();

// СТАЛО
private readonly ObservableCollection<ChatItem> _chatItems = new();
```

---

### Шаг 1.3 — Заменить ChatLogLabel на CollectionView

В `MainPage.xaml` заменить текущий ScrollView с Label:

```xml
<!-- БЫЛО -->
<ScrollView Grid.Row="2" x:Name="ChatScrollView" Padding="14,10">
    <Label x:Name="ChatLogLabel" Text="" LineBreakMode="WordWrap"
           FontSize="{OnIdiom Phone=12, Default=13}"
           TextColor="{StaticResource SeaShell}" />
</ScrollView>

<!-- СТАЛО -->
<CollectionView Grid.Row="2" x:Name="ChatList"
                ItemsSource="{Binding ChatItems}"
                Padding="10,8">
    <CollectionView.ItemTemplate>
        <DataTemplate x:DataType="local:ChatItem">
            <Grid Padding="0,3">
                <!-- Текстовое сообщение -->
                <Label Text="{Binding Text}"
                       IsVisible="{Binding IsVoice, Converter={StaticResource InvertBool}}"
                       LineBreakMode="WordWrap"
                       FontSize="{OnIdiom Phone=12, Default=13}"
                       TextColor="{StaticResource SeaShell}" />
                <!-- Голосовое сообщение -->
                <Grid IsVisible="{Binding IsVoice}"
                      ColumnDefinitions="Auto,*" ColumnSpacing="8">
                    <Button Grid.Column="0" Text="Play"
                            BackgroundColor="{StaticResource AccentBlue}"
                            TextColor="{StaticResource SeaShell}"
                            FontSize="10" Padding="10,4" CornerRadius="3"
                            CommandParameter="{Binding VoicePath}"
                            Clicked="OnVoiceItemPlayClicked" />
                    <Label Grid.Column="1" Text="{Binding Text}"
                           FontSize="11" TextColor="{StaticResource SeaShellMuted}"
                           VerticalOptions="Center" />
                </Grid>
            </Grid>
        </DataTemplate>
    </CollectionView.ItemTemplate>
</CollectionView>
```

Добавить в ресурсы страницы конвертер и namespace:
- `xmlns:local="clr-namespace:MassangerMaximka"` в `ContentPage`
- `InvertBoolConverter` (стандартный MAUI или своя реализация в 3 строки)

---

### Шаг 1.4 — Обновить AppendChat и добавить AppendVoiceMessage

```csharp
// Обновить AppendChat — добавляет ChatItem с текстом
private void AppendChat(string text)
{
    _chatItems.Add(new ChatItem { Text = text });
    // скроллим вниз
    MainThread.BeginInvokeOnMainThread(() =>
        ChatList.ScrollTo(_chatItems[^1], ScrollToPosition.End, animate: false));
}

// Новый метод для голосовых
private void AppendVoiceMessage(string filePath, bool fromPeer)
{
    var name = Path.GetFileNameWithoutExtension(filePath);
    var prefix = fromPeer ? "Received" : "Sent";
    _chatItems.Add(new ChatItem
    {
        Text = $"{prefix} voice: {name}",
        VoicePath = filePath
    });
}
```

---

### Шаг 1.5 — Обработчик кнопки Play у голосового сообщения

```csharp
private void OnVoiceItemPlayClicked(object? sender, EventArgs e)
{
    if (sender is Button btn && btn.CommandParameter is string path)
        PlayVoiceFile(path);
}
```

---

### Шаг 1.6 — Отображать отправленные голосовые в чате

В `OnStopSendVoiceClicked`, после успешной отправки:

```csharp
// БЫЛО
AppendChat($"[Voice] Sent voice message to {toNodeId}");

// СТАЛО
AppendChat($"[Voice] Sending to {toNodeId}...");
AppendVoiceMessage(path, fromPeer: false);
```

---

## Задача 2 — Убрать глобальную кнопку PLAY из шапки

**Файл:** `MassangerMaximka/MassangerMaximka/MainPage.xaml`

Убрать кнопку PLAY из шапки чата (Grid Row="0"):

```xml
<!-- Удалить эту кнопку из Grid ColumnDefinitions="*,Auto,Auto,Auto,Auto" -->
<Button Grid.Column="3" Text="PLAY"
        Clicked="OnPlayVoiceClicked" ... />
```

Также убрать Grid.Column="3" у кнопки Call (стало Column="3" вместо "4"), и пересчитать ColumnDefinitions с 5 колонок на 4.

`OnPlayVoiceClicked` в cs-файле трогать не нужно — просто перестанет вызываться из XAML.

---

## Задача 3 — BindingContext для CollectionView

**Файл:** `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

Если ChatList использует `{Binding ChatItems}`, нужно:
1. Сделать `ChatItems` публичным свойством:
```csharp
public ObservableCollection<ChatItem> ChatItems { get; } = new();
```
2. В конструкторе `MainPage()`:
```csharp
BindingContext = this;
```

Либо привязать напрямую в коде-behind без Binding:
```csharp
ChatList.ItemsSource = _chatItems;
```
(второй вариант проще, рекомендуется для скорости)

---

## Зависимости

- Задача 1 частично зависит от того, как PERSON_1 изменит `OnFileReceived` (они его не трогают — у них нет пересечений с этим методом).
- Задача 2 не зависит ни от чего.
- После Задачи 3 убедиться, что `OnClearChatClicked` очищает `_chatItems`, не `_chatLog`:
```csharp
private void OnClearChatClicked(object? sender, EventArgs e)
{
    _chatItems.Clear();
}
```
