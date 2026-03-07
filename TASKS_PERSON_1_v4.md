# PERSON_1 -- Sprint 4: Voice (Walkie-Talkie) + Message Delivery + Logging

## Goal
Fix **bidirectional walkie-talkie** so ALL devices hear ALL others, fix **message delivery reliability**, and add **color-coded error logging** (white = normal, red = errors/losses).

---

## Context: Why Voice Is One-Way

The walkie-talkie records audio on one device and sends PCM via UDP to the other.
The problem manifests as one device hearing the other but not vice-versa.

**Root causes identified (all must be fixed):**

| # | Cause | Detail |
|---|-------|--------|
| A | Android `ThrowIfNotSupported=true` may reject 16kHz on SOME devices | Fallback to 44100 + resample is present, but the `try/catch` only catches the outer exception; the inner `catch` swallows too broadly and may re-create the recorder incorrectly |
| B | `ParseWav` may read wrong sample rate if Android emits non-standard WAV header | Must validate parsed rate is sane (8000..96000), else default to 16000 |
| C | UDP keepalive sends empty `Data=[]` -- packet is exactly 12 bytes; some network stacks silently drop very small UDP packets | Increase keepalive payload to at least 20 bytes (pad with silence) |
| D | On Windows, `FileAudioSource.GetFilePath()` may return a valid path but 0-byte file if recording failed silently | Must check wavBytes.Length after reading file |
| E | Playback `CreatePlayer(new MemoryStream(wav))` can throw `NotSupportedException` on some platforms if WAV header is malformed | Wrap in try/catch, log but don't crash |

---

## Task 1 -- Robust PTT Recording (VoiceCallManager.cs)

### File: `MassangerMaximka/MassangerMaximka/VoiceCallManager.cs`

### 1.1 Fix `StartTalkingAsync` recording fallback chain

Current problem: if 16kHz throws, we create a NEW recorder and try 44100. This is correct, but we must also handle the case where BOTH throw.

```
CHANGE:
- Keep try 16kHz (ThrowIfNotSupported=true) -> catch -> try 44100 (ThrowIfNotSupported=false)
- Add explicit Log on which sample rate was actually used
- If BOTH fail, set _talking=false, log error with RED marker
```

### 1.2 Fix `StopTalkingAsync` WAV parsing safety

```
CHANGE:
- After ParseWav, validate info.SampleRate is in range [8000..96000]
  If not, log warning and assume 16000 (don't resample with garbage rate)
- After reading wavBytes from file, log the actual byte count
- If pcm.Length == 0 after parsing, log a RED error: "PTT: recorded file produced 0 bytes of PCM"
```

### 1.3 Guard playback errors in `PlaybackLoopAsync`

```
CHANGE:
- Wrap `player.Play()` in try/catch
- On failure: log RED "Playback failed: {ex.Message}", skip frame, continue loop
- Do NOT break the loop on playback errors
```

---

## Task 2 -- UDP Keepalive Fix (UdpVoiceTransport.cs)

### File: `HexTeam.Messenger.Core/Voice/UdpVoiceTransport.cs`

### 2.1 Increase keepalive packet size

Current: `Data = []` produces a 12-byte UDP packet. Some Android hotspot implementations drop very small UDP.

```
CHANGE:
- Set keepalive Data = new byte[160] (10ms of silence at 16kHz mono 16-bit)
- This produces a 172-byte packet, well above any minimum-size filter
- Receiver will get pcmData.Length=160, which passes Length==0 check
  but is only 10ms of silence -- inaudible
```

### 2.2 Increase initial burst to 5 packets

```
CHANGE:
- Burst count: 3 -> 5
- Burst interval: 50ms -> 30ms
- Total punch-through window: 150ms, covering typical NAT timeout
```

### 2.3 Add send/receive counters to keepalive log

```
CHANGE:
- Every 10th keepalive, log: "Voice keepalive: sent={n} remote={EP}"
- On receive of any frame, if it's keepalive-size (<=160B), don't fire FrameReceived
  but increment a keepalive_rx counter and log every 10th
```

---

## Task 3 -- Message Delivery Reliability (TcpChatTransport.cs)

### File: `HexTeam.Messenger.Core/Transport/TcpChatTransport.cs`

### 3.1 Add delivery failure event

Currently `SendMessageAsync` catches exceptions and sets `DeliveryStatus.Failed`, but the UI only sees `Sent` or times out. The `Failed` status fires `DeliveryStatusChanged` but the UI doesn't show it visually.

```
CHANGE in SendMessageAsync:
- On catch: log the exception message via _logger.LogError (not Warning)
- Ensure msg.Status = DeliveryStatus.Failed fires the event AFTER the catch
```

### 3.2 Add connection health check before send

```
CHANGE in SendMessageAsync:
- Before _connectionService.SendAsync, verify the connection socket is still open:
    if (!_connectionService.IsConnected(toNodeId))
        throw new InvalidOperationException(...)
- Already present, but ALSO check after SendAsync completes (connection may drop mid-send)
```

### File: `HexTeam.Messenger.Core/Transport/PeerConnectionService.cs`

### 3.3 Improve `SendAsync` error detection

```
CHANGE:
- In SendAsync, after writing to the NetworkStream, catch IOException/SocketException
- On error: call RemoveConnection(peerNodeId) and fire PeerDisconnected
- This ensures stale connections are cleaned up immediately, not silently kept
```

---

## Task 4 -- Color-Coded Error Logging (MainPage.xaml.cs)

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 4.1 Modify `TechLog` to support error-level coloring

```
CHANGE the TechLog method:
- Add an optional parameter: bool isError = false
- If isError == true: color = Colors.Red REGARDLESS of LogCat category
- If isError == false: normal white for all categories (simplify the color switch)
```

New color scheme per user request:
```
- Normal logs: WHITE (#FFFFFF)
- Error/loss logs: RED (#FF5252)
```

### 4.2 Add `TechLogError` convenience method

```csharp
private void TechLogError(LogCat cat, string text) => TechLog(cat, text, isError: true);
```

### 4.3 Update all error/failure log sites to use RED

Replace every TechLog call that reports a failure/error/loss with TechLogError:
- `SendMessageAsync` catch blocks
- `Voice call start error`
- `File send failed`
- `Playback error`
- `PTT record error` / `PTT send error`
- Delivery status `Failed`
- `Voice receive error`
- Connection failures

Keep all other logs WHITE.

---

## Task 5 -- Voice Call Signal Diagnostics

### File: `MassangerMaximka/MassangerMaximka/MainPage.xaml.cs`

### 5.1 Log the full voice endpoint pair when call starts

```
CHANGE in StartCallAsync:
- Log: "Voice call: LOCAL :{localPort} -> REMOTE {ip}:{voicePort}"
- Log the same on the callee side (OnAcceptCallClicked)
```

### 5.2 Log when first voice frame is sent and received

```
CHANGE in VoiceCallManager:
- On first SendFrameAsync call, log: "Voice TX: first frame sent to {EP}, {N} bytes"
- On first OnFrameReceived, log: "Voice RX: first frame received, {N} bytes"
- These help diagnose which direction is broken
```

---

## Milestone Checklist

- [ ] `StartTalkingAsync`: proper fallback chain with logging
- [ ] `StopTalkingAsync`: ParseWav validation, size logging
- [ ] `PlaybackLoopAsync`: exception-safe playback
- [ ] Keepalive: 160-byte payload, 5-packet burst
- [ ] Keepalive: filter keepalive-sized frames from playback
- [ ] `SendMessageAsync`: explicit error logging and Failed status
- [ ] `SendAsync`: IOException/SocketException -> disconnect
- [ ] `TechLog`: white=normal, red=errors
- [ ] `TechLogError` helper used at all error sites
- [ ] Voice signal diagnostic logs (endpoint pairs, first frame)
- [ ] Build passes, 74 tests pass

---

## Files Modified (Person 1 scope)

| File | Changes |
|------|---------|
| `VoiceCallManager.cs` | Recording fallback, ParseWav validation, playback safety, first-frame logging |
| `UdpVoiceTransport.cs` | Keepalive payload size, burst count, keepalive filtering |
| `WavHelper.cs` | No changes needed (already fixed in v3.5) |
| `TcpChatTransport.cs` | Send error logging, delivery failure |
| `PeerConnectionService.cs` | SendAsync error -> disconnect |
| `MainPage.xaml.cs` | TechLog colors (white/red), TechLogError, voice diagnostics |
