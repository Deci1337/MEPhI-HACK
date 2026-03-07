# PERSON_2 — Sprint 3

## Goal
Implement the **Channel UI** (invite flow, member list, connection count, disconnect notices)
and **channel-aware PTT** (all members see who is talking).
Depends on PERSON_1 having merged `ChannelService` before UI wiring.

---

## Task 1 — Channel UI: creation & invite flow

### 1.1 Add "Channel" button to `MainPage.xaml`
Place in the peers panel header row:
```xml
<Button x:Name="CreateChannelBtn" Text="Channel"
        BackgroundColor="#1E1E1E" TextColor="White"
        Clicked="OnCreateChannelClicked"/>
```

### 1.2 `OnCreateChannelClicked` in `MainPage.xaml.cs`
- Show a dialog: `await DisplayPromptAsync("New Channel", "Channel name:")`.
- Call `_channelService.CreateChannel(name)`.
- Show invite picker (see 1.3).

### 1.3 Invite picker
- Show `await DisplayActionSheet("Invite to channel", "Cancel", null, peerNames)`.
- For each selected peer: `_channelService.SendInvite(nodeId)`.
- Log: `TechLog(LogCat.Protocol, $"Channel invite sent to {name}")`.

### 1.4 Incoming invite banner
- Subscribe `_channelService.InviteReceived += OnChannelInviteReceived`.
- `OnChannelInviteReceived`: show `DisplayAlert` with Accept/Decline buttons.
- On Accept: `_channelService.JoinChannel(channelId, fromNodeId)`, switch chat to channel view.

---

## Task 2 — Channel chat view

### 2.1 Channel chat is a shared `ObservableCollection<ChatItem>`
- In `MainPage.xaml.cs` add:
  ```csharp
  private ObservableCollection<ChatItem>? _channelChat;
  private string? _activeChannelId;
  ```
- `SwitchToChannel()`: set `_channelChat = new(...)`, point `ChatList.ItemsSource` to it.

### 2.2 Route outgoing messages to channel
- When `_activeChannelId != null`, `OnSendClicked` sends the text to **each member** via `_chat.SendMessageAsync(memberId, text)`.
- Prefix in chat: `Me (channel): {text}`.

### 2.3 Route incoming messages to channel chat
- In `OnMessageReceived`, if sender is a channel member and `_activeChannelId` matches, route to `_channelChat`.

---

## Task 3 — Member count + disconnect notification

### 3.1 Channel header label
Add to XAML (above `ChatList`):
```xml
<Label x:Name="ChannelInfoLabel" IsVisible="False"
       Text="Channel: — | Members: 0"
       TextColor="#AAAAAA" FontSize="12" Margin="4,0"/>
```

### 3.2 Subscribe `_channelService.MembersUpdated`
```csharp
private void OnChannelMembersUpdated(List<string> members)
{
    MainThread.BeginInvokeOnMainThread(() =>
    {
        ChannelInfoLabel.IsVisible = true;
        ChannelInfoLabel.Text = $"Channel: {_activeChannelId} | Members: {members.Count}";
    });
}
```

### 3.3 Member disconnect in channel chat
- When `OnPeerDisconnected` fires for a node that is a channel member:
  - Remove from `ChannelService` member list via `_channelService.HandleMemberLeave(nodeId)`.
  - Append to channel chat: `"[Channel] {name} disconnected"`.

---

## Task 4 — Channel PTT (all members see who is talking)

### 4.1 Send PTT_START / PTT_END to all channel members
Currently PTT signals go only to `_callPeerNodeId`. When in a channel:
```csharp
private async void OnPttPressed(object? sender, EventArgs e)
{
    // ... existing code ...
    if (_activeChannelId != null)
        foreach (var m in _channelService.Members)
            _ = SendCallSignalAsync(m, PttStartSignal);
    else
        _ = SendCallSignalAsync(_callPeerNodeId, PttStartSignal);
}
```
Same for `OnPttReleased` with `PttEndSignal`.

### 4.2 Channel voice: multicast UDP
- When a channel call is active, `VoiceCallManager.StartTalkingAsync()` sends PCM chunks to **every member** in parallel:
  ```csharp
  // in StopTalkingAsync, after chunking:
  foreach (var ep in _remoteEndPoints)   // List<IPEndPoint>
      await _transport.SendFrameAsync(chunk, ep);
  ```
- `UdpVoiceTransport.SendFrameAsync` needs an optional `IPEndPoint? override` parameter.

### 4.3 PTT status overlay in XAML
```xml
<Label x:Name="ChannelPttLabel" IsVisible="False"
       TextColor="#FF9800" FontSize="13" Margin="4,0"/>
```
Show `"{Name} is talking..."` when `PttStateChanged` fires for a channel member.
Hide when `PttEndSignal` arrives.

---

## Task 5 — Polish & edge cases
- If the user is **not** in a channel, all existing 1-to-1 behavior is unchanged.
- If a channel member declines the invite, show toast: `"{Name} declined"`.
- Channel ID is shown as a short code (first 8 chars of GUID) in `ChannelInfoLabel`.
- Leaving the channel (`EndCall` or app `OnDisappearing`): send `ChannelLeave` to all members.

---

## Milestone checklist
- [ ] "Channel" button + create dialog
- [ ] Invite picker + `SendInvite` call
- [ ] Incoming invite banner + join flow
- [ ] `_channelChat` collection + `SwitchToChannel`
- [ ] Outgoing message fan-out to all members
- [ ] Incoming message routing to channel chat
- [ ] `ChannelInfoLabel` + `MembersUpdated` handler
- [ ] Disconnect notice in channel chat
- [ ] Channel PTT broadcast to all members
- [ ] `ChannelPttLabel` overlay
- [ ] `ChannelLeave` on disconnect / app exit
