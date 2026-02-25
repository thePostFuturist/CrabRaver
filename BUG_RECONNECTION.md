# ~~Bug Report: Bridge MCP WebSocket Reconnection Fails Silently~~

## RESOLVED (2026-02-24)

All 4 fixes implemented in commits leading to Phase 2.4 (`748caa0e3`). Connection is now self-healing across Unity play mode transitions, domain reloads, and passive disconnects. Keepalive timeout detection added in Phase 3.

---

*Original report below for historical reference:*

**Severity**: ~~HIGH~~ RESOLVED
**Component**: `Tools/DigitRaverHelperMCP/BridgeWebSocketClient.cs`
**Symptom**: After WebSocket reconnection, tool calls fail with `"Not connected to Bridge at ws://localhost:18800"`

---

## Reproduction

1. Start MCP server + Unity Bridge
2. Tool calls work normally
3. Bridge disconnects and reconnects (e.g., Unity domain reload, Play Mode toggle)
4. Next tool call → `"Not connected to Bridge"` error
5. `connection_status` shows `connected: false, reconnectCount: 0`

---

## Root Causes

### 1. `DispatchAsync` bails out instead of triggering reconnect

**File**: `BridgeToolRegistry.cs:141-149`

```csharp
// Check connection
if (!client.IsConnected)
{
    return new CallToolResult
    {
        Content = [new TextContentBlock { Text = $"Not connected to Bridge..." }],
        IsError = true
    };
}
```

**Problem**: When `IsConnected` is false at dispatch time, the registry **returns an error immediately** without attempting reconnection. Only `SendCommandAsync` has reconnection logic (line 111), but `DispatchAsync` never reaches it because it short-circuits first.

**Fix**: Remove the early bail-out in `DispatchAsync` (or replace it with a reconnection attempt), and let `SendCommandAsync`'s built-in reconnect logic handle it.

---

### 2. `ReconnectAsync` doesn't await receive loop readiness

**File**: `BridgeWebSocketClient.cs:279-285`

```csharp
private async Task ReconnectAsync()
{
    _logger.LogInformation("Attempting reconnection...");
    _receiveCts?.Cancel();       // Cancel old loop
    _reconnectCount++;
    await ConnectAsync();        // Returns BEFORE receive loop is running
}
```

**Problem**: `ConnectAsync` (line 84-88) fires `Task.Run(() => ReceiveLoopAsync(...))` and returns immediately. There's no synchronization to confirm the receive loop is actually running. If `SendCommandAsync` sends a message before `ReceiveLoopAsync` enters its first `ReceiveAsync` call, the response may never be matched.

**Fix**: Add a `TaskCompletionSource` or `ManualResetEventSlim` that `ReceiveLoopAsync` signals on its first iteration, and have `ConnectAsync` await it before returning.

---

### 3. `ReconnectAsync` doesn't await old receive loop termination

**File**: `BridgeWebSocketClient.cs:282`

```csharp
_receiveCts?.Cancel();   // signals old loop to stop
// ...but never awaits _receiveTask to confirm it stopped
```

**Problem**: The old receive loop may still be running (draining its current `ReceiveAsync`) when the new socket and receive loop start. Two receive loops can briefly coexist, and the old one's cleanup (lines 216-222) fails all pending requests — including requests just queued by the new loop.

**Fix**: After cancelling `_receiveCts`, await `_receiveTask` (with a short timeout) before creating the new socket.

---

### 4. No automatic reconnect when connection drops passively

The receive loop (line 165) exits when `_ws.State != Open`, but it never triggers a reconnect — it just logs "Receive loop ended" (line 224) and stops. The client sits in a permanently disconnected state until the next `SendCommandAsync` call happens to trigger `ReconnectAsync`.

**Problem**: If the Bridge drops (Unity reloads), the MCP server is silently dead. The keepalive loop (line 262) checks `IsConnected` and does nothing when false — it doesn't trigger reconnection either.

**Fix**: At the end of `ReceiveLoopAsync`, if the loop exited unexpectedly (not due to cancellation), trigger `ReconnectAsync` automatically.

---

## State Model Gap

The client has 4 distinct states but only distinguishes 2:

| State | IsConnected | Can Process? | Handled? |
|-------|-------------|-------------|----------|
| Disconnected | false | NO | YES |
| Connected + receive loop running | true | YES | YES |
| Reconnecting (between old/new loop) | varies | NO | NO |
| Connected + loop still spinning up | true | NO | NO |

---

## Suggested Fixes (Priority Order)

### Fix 1 — Let DispatchAsync trigger reconnect (quick win)
```csharp
// BridgeToolRegistry.cs:141-149 — replace early bail-out
if (!client.IsConnected)
{
    _logger.LogInformation("Connection lost, attempting reconnect for {Tool}...", toolName);
    // Fall through to ExecuteBridgeCommand → SendCommandAsync will reconnect
}
```
Or simply remove the `IsConnected` check entirely since `SendCommandAsync` already handles it.

### Fix 2 — Await receive loop readiness in ConnectAsync
```csharp
// BridgeWebSocketClient.cs — add field
private TaskCompletionSource<bool>? _receiveReady;

// In ConnectAsync, after starting receive loop:
_receiveReady = new TaskCompletionSource<bool>();
_receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
await _receiveReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

// In ReceiveLoopAsync, at start of while loop (first iteration):
_receiveReady?.TrySetResult(true);
```

### Fix 3 — Await old loop before reconnecting
```csharp
// BridgeWebSocketClient.cs:ReconnectAsync
_receiveCts?.Cancel();
if (_receiveTask != null)
    await Task.WhenAny(_receiveTask, Task.Delay(3000)); // wait up to 3s
_reconnectCount++;
await ConnectAsync();
```

### Fix 4 — Auto-reconnect from receive loop
```csharp
// BridgeWebSocketClient.cs — end of ReceiveLoopAsync (after line 224)
if (!ct.IsCancellationRequested && !_disposed)
{
    _logger.LogInformation("Connection dropped, scheduling reconnect...");
    _ = Task.Run(async () =>
    {
        await Task.Delay(1000);
        await ReconnectAsync();
    });
}
```

---

## Files to Modify

| File | Lines | Change |
|------|-------|--------|
| `BridgeToolRegistry.cs` | 141-149 | Remove/replace early `IsConnected` bail-out |
| `BridgeWebSocketClient.cs` | 61-105 | Add receive-loop-ready signal in `ConnectAsync` |
| `BridgeWebSocketClient.cs` | 161-225 | Signal readiness + trigger auto-reconnect on drop |
| `BridgeWebSocketClient.cs` | 279-285 | Await old loop termination in `ReconnectAsync` |
