# PERSON_1 — Backend / Voice / Call Fixes

Все задачи независимы от PERSON_2. Не трогаем MainPage.xaml и AppendChat.

---

## Задача 1 — Исправить запись в VoiceCallManager (CRITICAL)

**Файл:** `MassangerMaximka/MassangerMaximka/VoiceCallManager.cs`

**Симптом:** `[SYS] Record error: Unable to find the specified file` — спам каждые ~100 мс во время звонка.

**Root cause:** `recorder.StartAsync(path)` — на Windows `Plugin.Maui.Audio` не гарантирует запись по указанному пути. Файл либо создаётся в другом месте, либо вызов падает сразу с FileNotFoundException.

**Что менять в `RecordLoopAsync`:**
```csharp
// БЫЛО
var path = Path.Combine(_tempDir, $"c_{_chunkIndex++}.wav");
var recorder = _audioManager.CreateRecorder();
await recorder.StartAsync(path);
await Task.Delay(ChunkMs, ct);
var source = await recorder.StopAsync();
var filePath = source is FileAudioSource fs ? fs.GetFilePath() : path;

// СТАЛО — использовать options, не path
var recorder = _audioManager.CreateRecorder();
var options = new AudioRecorderOptions
{
    SampleRate = 16000,           // снизить для меньшей задержки
    Channels = ChannelType.Mono,
    BitDepth = BitDepth.Pcm16bit,
    Encoding = Plugin.Maui.Audio.Encoding.Wav,
    ThrowIfNotSupported = false   // не кидать исключение на эмуляторе
};
await recorder.StartAsync(options);
await Task.Delay(ChunkMs, ct);
var source = await recorder.StopAsync();

// получить реальный путь от source
string? filePath = null;
if (source is FileAudioSource fsa)
    filePath = fsa.GetFilePath();
if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) continue;
```

Убрать поле `_chunkIndex` — оно больше не нужно.

---

## Задача 2 — Hardcoded voice port при исходящем звонке

**Файл:** `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

**Симптом:** при исходящем звонке `_callPeerVoicePort = 45679` захардкожен. Если удалённый узел занял 45679 и слушает на 45680+ (логика `BindUdpClient` пробует offset), UDP-фреймы уходят не туда.

**Где:** метод `OnCallButtonClicked`, строка ~627.

**Что менять:**
```csharp
// БЫЛО
_callPeerVoicePort = 45679;

// СТАЛО — порт берём из конфига нашего же транспорта и передаём в CALL_REQUEST
// Получатель сообщает нам свой voice port в CALL_ACCEPT, но до этого используем
// стандартный порт из NodeConfiguration:
var config = MauiProgram.AppInstance?.Services.GetService<NodeConfiguration>();
_callPeerVoicePort = config?.VoicePort ?? 45679;
```

В `OnMessageReceived` при разборе `CALL_ACCEPT` нужно также читать voice_port из ответа, если добавите его в CALL_ACCEPT payload (опционально, но правильно).

---

## Задача 3 — Хранить IP подключившегося пира

**Файлы:**
- `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`
- Возможно `HexTeam.Messenger.Core/Transport/` (если нужно пробросить endpoint)

**Симптом:** `ResolvePeerIpAddress(nodeId)` не может найти IP пира, который подключился сам (не через ручной ввод и не через discovery). Звонок падает с `[Error] Cannot resolve peer IP address`.

**Что нужно:**

1. Добавить словарь в поля `MainPage`:
```csharp
private readonly Dictionary<string, System.Net.IPEndPoint> _peerEndpointMap = new();
```

2. В `OnPeerConnected(string nodeId)` — получить endpoint от сервиса и сохранить.  
   Проверить, есть ли у `PeerConnectionService` метод/свойство `GetPeerEndPoint(nodeId)` или `ConnectedPeers` с endpoint'ами. Если нет — добавить его в Core.

3. В `ResolvePeerIpAddress(nodeId)` добавить первым приоритетом:
```csharp
if (_peerEndpointMap.TryGetValue(nodeId, out var ep))
    return ep.Address;
```

4. Если в `PeerConnectionService` нет способа получить IP — добавить event `PeerConnectedWithEndPoint(string nodeId, IPEndPoint ep)` или словарь `ConnectedPeerEndPoints`.

**Файл для проверки:** `HexTeam.Messenger.Core/Transport/` — найти где хранятся TCP соединения.

---

## Задача 4 — Эмулятор: тихая запись голоса (диагностика)

**Файл:** `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

**Симптом:** на Android-эмуляторе запись идёт (файл создаётся), но при воспроизведении тишина.

**Причина:** эмулятор не пробрасывает микрофон хоста по умолчанию.

**Что сделать в коде** (уже частично есть, нужно улучшить):
1. После `StopAsync()` в `OnStopSendVoiceClicked` — логировать размер файла и первые байты после WAV-заголовка:
```csharp
var bytes = File.ReadAllBytes(path);
var dataBytes = bytes.Skip(44).Take(20).ToArray();
var isAllZero = dataBytes.All(b => b == 0);
TechLog(LogCat.System, $"Voice file: {bytes.Length}B, silence={isAllZero}");
```
Если `silence=True` — это эмулятор, а не баг кода.

2. В AVD Manager → Edit → Advanced → выставить `Microphone: Virtual microphone uses host audio input`.

**Итог:** это не баг кода, это конфигурация эмулятора. Задача — добавить диагностику и предупреждение в лог.

---

## Зависимости

- Задача 3 может потребовать изменений в `HexTeam.Messenger.Core` — обсуди с PERSON_2 чтобы не конфликтовать с UI-изменениями.
- Задачи 1 и 2 полностью в `VoiceCallManager.cs` и `MainPage.xaml.cs` — не затрагивают XAML.
- После Задачи 1 — протестировать звонок: в лог должен исчезнуть спам `Record error`.
