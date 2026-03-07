# PERSON_1 — Sprint 3

## Goal
Implement **image sharing in chat** (photo appears inline, not as file link)
and the **Core / signaling layer** for group channels.

---

## Task 1 — Image message type (Core)

### 1.1 Add `ImagePacket` to Core
File: `HexTeam.Messenger.Core/Transport/TransportEnvelope.cs`
- Add `Image = 15` to `TransportPacketType` enum.

File: `HexTeam.Messenger.Core/Transport/` — new file `ImagePacket.cs`
```csharp
public sealed class ImagePacket
{
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "image/jpeg";
    public byte[] Data   { get; set; } = [];
}
```
- Serialize with `JsonSerializer` + UTF-8 → `Payload` bytes.

### 1.2 Send method in `TcpChatTransport`
```csharp
Task SendImageAsync(string toNodeId, string fileName, byte[] imageBytes);
```
- Build envelope `Type = Image`, payload = JSON-serialized `ImagePacket`.
- Cap image size at **2 MB** (reject and log if larger).

### 1.3 Receive event
```csharp
event Action<TransportImageMessage>? ImageReceived;
// TransportImageMessage { string FromNodeId; string FileName; string MimeType; byte[] Data; }
```
- Parse in `TcpChatTransport` receive loop when `Type == Image`.

---

## Task 2 — Image display in `ChatItem` & XAML

### 2.1 Extend `ChatItem.cs`
```csharp
public byte[]? ImageBytes { get; init; }
public bool IsImage => ImageBytes != null;
public bool IsText  => VoicePath == null && ImageBytes == null;
```

### 2.2 `MainPage.xaml` — add image template in `DataTemplate`
Below the text block add:
```xml
<Image IsVisible="{Binding IsImage}"
       HeightRequest="200" WidthRequest="200"
       Aspect="AspectFill">
    <Image.Source>
        <StreamImageSource>
            <StreamImageSource.Stream>
                <x:Null/>
            </StreamImageSource.Stream>
        </StreamImageSource>
    </Image.Source>
</Image>
```
Use a `Converter` (`ByteArrayToImageSourceConverter`) bound to `ImageBytes`.

Converter (new file `Converters/ByteArrayToImageSourceConverter.cs`):
```csharp
public sealed class ByteArrayToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, ...) =>
        value is byte[] b && b.Length > 0
            ? ImageSource.FromStream(() => new MemoryStream(b))
            : null;
    public object ConvertBack(...) => throw new NotSupportedException();
}
```

### 2.3 `MainPage.xaml.cs` — wire up
- Subscribe `_chat.ImageReceived += OnImageReceived`.
- `OnImageReceived`: `AppendChat` with a new overload that takes `byte[] imageBytes`, creates `ChatItem { ImageBytes = data, Text = fileName }`.
- Add **"Send Photo"** button next to "Send File" button (reuse file picker, filter for images).
- In `OnSendPhotoClicked`: pick file → read bytes → call `_chat.SendImageAsync`.

---

## Task 3 — Channel: Core protocol

### 3.1 New packet types (add to enum)
```
ChannelInvite  = 70,
ChannelJoin    = 71,
ChannelLeave   = 72,
ChannelMembers = 73,   // broadcast current member list
ChannelPtt     = 74,   // who is currently talking in channel
```

### 3.2 `ChannelPacket.cs` (Core, new file)
```csharp
public sealed class ChannelPacket
{
    public string ChannelId  { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public List<string> MemberNodeIds { get; set; } = [];
}
```

### 3.3 `ChannelService.cs` (Core, new file)
Responsibilities:
- `CreateChannel(string name)` — generate `ChannelId = Guid.NewGuid().ToString("N")[..8]`, add self to members.
- `SendInvite(string toNodeId)` — send `ChannelInvite` envelope via `TcpChatTransport`.
- `HandleInvite(ChannelPacket)` — raise `InviteReceived` event.
- `JoinChannel(string channelId, string fromNodeId)` — send `ChannelJoin`.
- `BroadcastMembers()` — send `ChannelMembers` to all known member nodes.
- `HandleMemberLeave(string nodeId)` — remove from list, re-broadcast.
- Events: `InviteReceived`, `MembersUpdated`, `PttStateChanged`.

### 3.4 Register in `ServiceCollectionExtensions`
```csharp
services.AddSingleton<ChannelService>();
```

---

## Milestone checklist
- [ ] `ImagePacket` + enum value
- [ ] `SendImageAsync` / `ImageReceived` in transport
- [ ] `ByteArrayToImageSourceConverter`
- [ ] `ChatItem.IsImage` + XAML image template
- [ ] "Send Photo" button + handler
- [ ] Channel packet types + `ChannelPacket`
- [ ] `ChannelService` CRUD + events
- [ ] DI registration
