# PERSON_2 -- Sprint 4: Connection Stability + Chat Switching + UX

## Goal
Fix **auto-switch bug** (new device steals focus from current chat), fix **chat confusion** when multiple peers connect, ensure **messages always arrive** to the correct chat, and parallel incoming messages from any device are stored even when viewing another chat.

---

## Context: Why Chats Get Mixed Up

When a new peer connects, `OnPeerConnected` calls `PromoteConnectedPeer` which
unconditionally does `SwitchChat(actualNodeId)`. This forces the user out of
their current conversation into the newly connected peer's chat.

Also, when messages arrive from a non-active peer, they are stored but the
`[NEW]` indicator and chat routing can be unreliable because `_activeChatPeer`
gets overwritten by auto-switch.

---

## Task 1 -- Stop Auto-Switch on New Connection

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 1.1 Fix `OnPeerConnected` -- do NOT switch chat

Current code (lines 191-214):
```csharp
private void OnPeerConnected(string nodeId)
{
    MainThread.BeginInvokeOnMainThread(() =>
    {
        ...
        if (ep != null)
            PromoteConnectedPeer(ep, nodeId);   // <-- FORCES SwitchChat
        else if (_activeChannelId == null)
            SwitchChat(nodeId);                  // <-- FORCES SwitchChat
        ...
    });
}
```

**CHANGE to:**
```
- Cache endpoint, remember the peer, refresh list -- but do NOT switch chat
- Only auto-switch if _activeChatPeer is null (user has no active conversation)
- If user IS in a chat, just add [NEW] marker and refresh list
- Remove the PromoteConnectedPeer call from OnPeerConnected entirely
- Keep PromoteConnectedPeer only for MANUAL connect actions (OnConnectClicked, OnPeerDoubleTapped)
```

New logic:
```csharp
private void OnPeerConnected(string nodeId)
{
    MainThread.BeginInvokeOnMainThread(() =>
    {
        var ep = _connections?.GetPeerEndPoint(nodeId);
        if (ep != null)
        {
            _peerEndpointMap[nodeId] = ep;
            RememberPeer(ResolveDisplayName(nodeId), ep.Address.ToString(), ep.Port);
        }

        RefreshPeersList();
        _autoConnectInFlight.Remove(nodeId);
        AppendChat($"[Connected] {ResolveDisplayName(nodeId)}", toPeer: nodeId);

        // Only auto-open chat if user has no active conversation
        if (_activeChatPeer == null && _activeChannelId == null)
            SwitchChat(nodeId);
        else
        {
            _unreadPeers.Add(nodeId);
            RefreshPeersList();
        }

        TechLog(LogCat.Network, $"TCP connected: {ResolveDisplayName(nodeId)} ({nodeId})");
    });
}
```

### 1.2 Fix `PromoteConnectedPeer` -- make it NOT auto-switch

```
CHANGE:
- PromoteConnectedPeer should only merge identity (MergePeerIdentity)
  and return the actual nodeId
- Remove the SwitchChat call from inside PromoteConnectedPeer
- The callers (OnConnectClicked, OnPeerDoubleTapped) decide whether to switch
```

New:
```csharp
private string PromoteConnectedPeer(IPEndPoint endPoint, string fallbackNodeId)
{
    var actualNodeId = _connections?.FindPeerNodeId(endPoint) ?? fallbackNodeId;
    if (!string.Equals(actualNodeId, fallbackNodeId, StringComparison.Ordinal))
        MergePeerIdentity(fallbackNodeId, actualNodeId);
    _selectedPeerNodeId = actualNodeId;
    return actualNodeId;
}
```

Then in `OnConnectClicked` and `OnPeerDoubleTapped`, after PromoteConnectedPeer:
```csharp
var connectedNodeId = ok ? PromoteConnectedPeer(endPoint, nodeId) : nodeId;
if (ok) SwitchChat(connectedNodeId);   // explicit switch only on manual connect
```

---

## Task 2 -- Reliable Chat Routing for Multiple Peers

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 2.1 Incoming messages always go to the correct per-peer chat

Current `OnMessageReceived` (line 368-390):
```csharp
AppendChat($"{senderName}: {text}", toPeer: msg.FromNodeId);
if (_activeChatPeer != msg.FromNodeId && _activeChannelId == null)
{
    if (_activeChatPeer == null)
        SwitchChat(msg.FromNodeId);
    else
    {
        _unreadPeers.Add(msg.FromNodeId);
        RefreshPeersList();
    }
}
```

**Problem:** When `_activeChatPeer == null`, we auto-switch to the sender.
This can cause chat jumping when messages from different peers arrive rapidly.

**CHANGE:**
```
- Remove the auto-switch-on-first-message logic
- If _activeChatPeer is null, still DON'T auto-switch; just mark [NEW]
- The user must explicitly tap a peer to open their chat
- Exception: if there's only ONE connected peer total, auto-switch is OK
```

### 2.2 `AppendChat` with `toPeer:` parameter must ALWAYS store to correct chat

Verify that `AppendChat(text, toPeer: nodeId)` stores the message in
`_peerChats[nodeId]` even if `_activeChatPeer != nodeId`. This is already
the case, but double-check that the `GetOrCreatePeerChat(nodeId)` path
is always taken.

### 2.3 Images follow the same rule

`OnImageReceived` already does `AppendChat(..., toPeer: msg.FromNodeId)`.
Make sure it also marks `[NEW]` and refreshes -- already done, just verify.

---

## Task 3 -- Peer List [NEW] Indicator Reliability

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 3.1 `FormatPeerDisplay` must include [NEW] in the display string

Check that `FormatPeerDisplay` uses `_unreadPeers.Contains(nodeId)` to show
the `[NEW]` badge. If not, add it.

### 3.2 Refresh peer list on every incoming message

After `OnMessageReceived` stores the message and adds to `_unreadPeers`,
ensure `RefreshPeersList()` is called. Already done -- verify and keep.

### 3.3 Clear [NEW] on chat switch

`SwitchChat` already does `_unreadPeers.Remove(nodeId)` -- verify and keep.

---

## Task 4 -- Connection State Clarity

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 4.1 Show connection state per peer in the list

```
CHANGE FormatPeerDisplay:
- Connected peers: show GREEN dot or prefix "[OK]"
- Discovered but not connected: show GRAY prefix "[?]"
- Saved peers (not connected): show dim prefix "[saved]"
```

This way the user clearly sees which peers are truly connected.

### 4.2 On disconnect, show RED notice in the peer's chat

```
CHANGE OnPeerDisconnected:
- AppendChat("[Disconnected] {name}", toPeer: nodeId) -- already done
- Also add: AppendChat("[!] Messages will not be delivered until reconnected", toPeer: nodeId)
- This clearly informs the user if they try to send to a stale peer
```

### 4.3 Prevent sending to disconnected peer with clear error

```
CHANGE OnSendClicked:
- Before calling _chat.SendMessageAsync, check:
    if (_connections?.IsConnected(toNodeId) != true)
    {
        AppendChat("[Error] Peer is not connected. Tap Connect first.");
        return;
    }
- This prevents the confusing "says connected but messages don't arrive"
```

---

## Task 5 -- Chat Switching Stability

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 5.1 SwitchChat must be idempotent and safe

```
VERIFY:
- SwitchChat(nodeId) early-returns if nodeId == _activeChatPeer
- It sets ChatList.ItemsSource to the correct collection
- It clears [NEW] for the switched peer
- It scrolls to the last message
```

### 5.2 Prevent race conditions on rapid switching

```
CHANGE:
- Add a guard: if SwitchChat is called while already switching, queue it
- Simple approach: use a _switchingChat flag
    private bool _switchingChat;
    private void SwitchChat(string nodeId)
    {
        if (_switchingChat || nodeId == _activeChatPeer) return;
        _switchingChat = true;
        try { ... existing logic ... }
        finally { _switchingChat = false; }
    }
```

### 5.3 Channel <-> P2P switch must be clean

```
VERIFY:
- SwitchToChannel() correctly sets _activeChannelId and ChatList.ItemsSource
- When switching BACK from channel to P2P, _activeChannelId is nulled
- SwitchChat sets _activeChannelId = null (already done at line 238)
```

---

## Task 6 -- Parallel Incoming Messages from Multiple Devices

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 6.1 Guarantee messages from ANY peer are stored

Even if the user is viewing Peer A's chat, messages from Peer B and Peer C
must be stored in their respective `_peerChats[B]` and `_peerChats[C]`.

```
VERIFY in OnMessageReceived:
- AppendChat($"{name}: {text}", toPeer: msg.FromNodeId)
  uses the toPeer parameter to route to the correct collection
- GetOrCreatePeerChat creates a new collection if it doesn't exist
- The message is NEVER lost regardless of which chat is active
```

### 6.2 When switching to Peer B's chat, ALL their messages must be visible

```
VERIFY:
- SwitchChat(B) sets ChatList.ItemsSource = _peerChats[B]
- This collection contains ALL messages ever received from B
- ScrollTo last message works
```

---

## Milestone Checklist

- [ ] `OnPeerConnected`: NO auto-switch when user has active chat
- [ ] `PromoteConnectedPeer`: no SwitchChat inside, only identity merge
- [ ] `OnConnectClicked` / `OnPeerDoubleTapped`: explicit SwitchChat after manual connect
- [ ] `OnMessageReceived`: no auto-switch to sender, always mark [NEW]
- [ ] Peer list shows connection state: [OK] / [?] / [saved]
- [ ] Disconnect notice in peer's chat
- [ ] Send blocked to disconnected peer with clear error
- [ ] `SwitchChat`: idempotent, race-safe, channel-clean
- [ ] Parallel messages stored in correct per-peer collections
- [ ] [NEW] indicator reliable: set on receive, cleared on switch
- [ ] Build passes, 74 tests pass

---

## Files Modified (Person 2 scope)

| File | Changes |
|------|---------|
| `MainPage.xaml.cs` | OnPeerConnected, PromoteConnectedPeer, OnMessageReceived, OnSendClicked, SwitchChat, FormatPeerDisplay, OnPeerDisconnected |

**No overlap with Person 1 files**: Person 1 modifies `VoiceCallManager.cs`, `UdpVoiceTransport.cs`, `TcpChatTransport.cs`, `PeerConnectionService.cs`, and the `TechLog`/voice sections of `MainPage.xaml.cs`. Person 2 modifies the connection/chat/peer-list sections of `MainPage.xaml.cs`. Coordinate if both touch `MainPage.xaml.cs` -- Person 2's changes are in the connection handlers (lines 191-260, 646-700, 760-790, 1697-1740), Person 1's changes are in TechLog (lines 1415-1465) and voice call methods (lines 1041-1240).
