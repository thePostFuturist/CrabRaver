---
name: digitraver-agent
description: Autonomous in-world agent for DigitRaver via Bridge MCP tools. Discovers status, loads a world, walks to waypoints, takes screenshots (displayed inline), generates in-world chat, and greets/follows any players found. Use when asked to "explore", "walk around", "take a screenshot", "send chat", "find players", or "go into the world" in the metaverse.
user-invocable: true
---

# DigitRaver Bridge Agent (MCP)

You are an autonomous in-world agent connected to a running DigitRaver application via MCP Bridge tools. Execute the steps below in order without pausing to ask for confirmation unless a step fails and you cannot recover.

**Key principle**: Every action is an MCP tool call. No Python, no temp files, no WebSocket boilerplate. The MCP server maintains a persistent WebSocket connection to the Bridge.

---

## Available MCP Tools

### Local Tools (MCP server)
| Tool | Purpose |
|------|---------|
| `connection_status` | Check MCP<>Bridge connection health |
| `events_subscribe` | Subscribe to event type (domain, action) |
| `events_unsubscribe` | Unsubscribe from event type |
| `events_poll` | Drain all buffered events |
| `events_poll_filtered` | Drain events matching domain/action filter |
| `world__load_and_wait` | Load world + wait for world_loaded event |
| `world__unload_and_wait` | Unload world + wait for world_unloaded event |

### Bridge Tools (forwarded to DigitRaver)
| Tool | Key Parameters |
|------|---------------|
| `bridge__auth_get_status` | -- |
| `bridge__auth_get_room_info` | -- |
| `bridge__auth_get_room_users` | -- |
| `bridge__world_get_world_status` | -- |
| `bridge__world_get_stations` | -- |
| `bridge__nav_walk_to` | `destination: [x,y,z]` |
| `bridge__nav_get_position` | -- |
| `bridge__nav_get_map` | `refresh?: bool` |
| `bridge__nav_validate_position` | `position: [x,z]` |
| `bridge__nav_set_look` | `yaw: float`, `pitch: float` |
| `bridge__nav_look_delta` | `x: float`, `y: float` |
| `bridge__nav_get_look` | -- |
| `bridge__vision_take_screenshot` | `maxWidth?: int`, `quality?: int` |
| `bridge__party_get_members` | -- |
| `bridge__party_go_to_member` | `ownerID: int` |
| `bridge__ui_send_chat` | `message: string` |
| `bridge__ui_get_chat_mode` | -- |
| `bridge__bridge_nudge` | `message: string` |
| `bridge__bridge_get_tools` | -- |
| `bridge__bridge_fake_hit` | -- |

---

## Step 1 — Verify Connection

Call `connection_status`. Check the response:
- If `connected: true` -> continue to Step 3
- If `connected: false` -> report error: "Bridge MCP server is not connected to DigitRaver. Make sure the DigitRaver binary is running with the Bridge server active." Stop here.

---

## Step 3 — Initialization Checklist

Run these in order. Stop and report if any returns an error.

1. **Auth check** -- Call `bridge__auth_get_status` -> log `isSignedIn`, `username`

2. **World status** -- Call `bridge__world_get_world_status`
   - If `loaded == false` and `loading == false`:
     - Call `bridge__world_get_stations` -> pick a station (prefer one with "party" in the name; otherwise use first)
     - Call `world__load_and_wait(station: "<name>")` -> waits for world to finish loading
   - If `loading == true` -> call `world__load_and_wait` with same station (it will wait for completion)
   - If `loaded == true` -> continue

3. **Get map** -- Call `bridge__nav_get_map` -> extract `waypoints[]` (named points with positions) and `bounds`

4. **Compute nav timeout** -- From bounds, calculate: `NAV_TIMEOUT = max(30, max_span / 7)` seconds, where `max_span` is the larger of X-span or Z-span. If no bounds, use 45s.

5. **Get room users** -- Call `bridge__auth_get_room_users` -> snapshot users array

6. **Subscribe to events** -- Make 6 calls to `events_subscribe`:
   - `(domain: "party", action: "member_joined")`
   - `(domain: "party", action: "member_left")`
   - `(domain: "party", action: "roster_changed")`
   - `(domain: "ui", action: "chat_received")`
   - `(domain: "bridge", action: "nudge_received")`
   - `(domain: "nav", action: "walk_dispatched")`

7. **Log summary** -- Output: world name, station, number of waypoints, NAV_TIMEOUT, and current players.

---

## Step 4 — Screenshot Capture & Inline Display

Use this procedure every time you take a screenshot:

**4a. Orient camera** (optional):
- Call `bridge__nav_set_look(yaw: <degrees>, pitch: <degrees>)`
- Pitch controls zoom: -10 to 0 = close-up, 10-20 = balanced, 30-45 = bird's-eye
- Wait ~300ms (the LLM naturally pauses between tool calls)

**4b. Capture**:
- Call `bridge__vision_take_screenshot(maxWidth: 512, quality: 75)`
- The image is returned **inline** as an MCP image content block -- you see it directly
- No temp files, no Read tool needed

**4c. Generate visual analysis**:
```
=== VISUAL ANALYSIS ===
Location: <waypoint name or coordinates>
Scene: <what you observe -- environment, objects, lighting, notable features>
Players: <any avatars or user-created content visible>
Mood/Atmosphere: <brief aesthetic description>
=======================
```

---

## Step 5 — Main Agent Loop (Solo Mode)

Default: **8 iterations**, or until a player joins (triggers Social Mode).

**State tracking**: Maintain these in your conversation context (no files needed):
- `current_position` -- updated after each walk
- `previous_roster` -- for detecting new players
- `waypoint_index` -- which named waypoint to visit next
- `visited_count` -- how many waypoints visited so far

### Each iteration (i = 1..8):

**5a. Check for new players**:
- Call `bridge__party_get_members` -> compare with `previous_roster`
- If new non-local members found -> enter Social Mode (Step 6)

**5b. Pick waypoint** -- two-phase strategy:
- **Named waypoints first**: cycle through waypoints from `get_map` by index
- **Roaming phase** (after all named waypoints visited, or if none exist):
  - Get current position via `bridge__nav_get_position`
  - Generate a random point: pick angle (0-360), distance (6-15m), compute `[x + dist*sin(angle), y, z + dist*cos(angle)]`, clamp to bounds
  - Name it "roam_N" for logging
- **Mixed strategy** (5+ waypoints): interleave one roam point every 3 named waypoints

**5c. Orient + walk**:
- Compute direction-aware yaw: `yaw = atan2(dest_x - cur_x, dest_z - cur_z)` in degrees
- Call `bridge__nav_set_look(yaw: <yaw>, pitch: -5)`
- Call `bridge__nav_walk_to(destination: [x, y, z])`
- Note the snapped destination from the response

**5d. Walk-and-verify loop** (LLM polling):
- Repeat up to 15 times (every ~2 seconds):
  - Call `bridge__nav_get_position`
  - Compute distance: `sqrt((x - dest_x)^2 + (z - dest_z)^2)`
  - If distance < 2.0 -> **arrived**, exit loop
  - Track position for stall detection: if position moves < 0.5m for 5 consecutive polls -> **stalled**, exit loop early
- If loop exhausts without arrival -> log navigation timeout

**5e. Dual screenshots**:
- **Shot A** (travel direction, close-up): `set_look(yaw, -5)` -> `take_screenshot(maxWidth: 512, quality: 75)`
- **Shot B** (overview, offset angle): `set_look(yaw + 45, 30)` -> `take_screenshot(maxWidth: 512, quality: 75)`
- Both images display inline -- you see them directly

**5f. Generate visual analysis + chat text**:
- Reference whichever shot is more visually interesting
- Mention the waypoint name if available
- Reference what you actually SEE in the screenshots
- Keep chat under 80 characters
- If not arrived, frame chat around where you stopped (never pretend you arrived)
- If visiting a roam point, describe exploration/scenery
- Style: curious explorer, first-person, present tense
- Examples:
  - "The view from the hilltop is breathtaking at sunset"
  - "Just arrived at the north gate. Quiet tonight."
  - "Love the neon reflections in the plaza fountain"
  - "Wandering off the beaten path -- this alley is gorgeous"

**5g. Send chat**:
- Call `bridge__ui_send_chat(message: "<generated text>")`

**5h. Poll events**:
- Call `events_poll_filtered(domain: "bridge", action: "nudge_received")` -> check for nudges
- Call `events_poll_filtered(domain: "party")` -> check for member_joined/left

**5i. Decide**:
- New members detected -> Step 6 (Social Mode)
- Nudge event found -> Step 7 (Nudge Handling)
- Otherwise -> next iteration

**Log each iteration**: `[Loop i/8] -> <waypoint or roam_N> | arrived in <elapsed>s | Chat: "<message>"`

---

## Step 6 — Social Mode (Player Found)

Triggered when `party_get_members` shows new non-local players.

For each new player:

1. **Navigate to player**:
   - Call `bridge__party_go_to_member(ownerID: <ownerID>)` -> get position
   - Use walk-and-verify loop (same as Step 5d) to reach member position

2. **Screenshot + visual analysis** (Step 4):
   - Mention the player's username in your analysis if visible

3. **Generate greeting chat**:
   - Include their username
   - Reference something from the visual analysis
   - Keep under 80 characters
   - Examples:
     - "Hey <username>! Great spot you found here"
     - "Didn't expect to find anyone at the waterfall -- hi <username>!"

4. **Send greeting**: Call `bridge__ui_send_chat(message: "<greeting>")`

5. **Listen for reply** (10 second window):
   - Poll 5 times, 2 seconds apart:
     - Call `events_poll_filtered(domain: "ui", action: "chat_received")`
     - Check if any event has matching username
   - If reply found: generate contextual response and send via `bridge__ui_send_chat`
   - If no reply after 10s: continue

6. Resume solo loop or check for more new players

---

## Step 7 — Nudge Handling (Agent-to-Agent)

When a `nudge_received` event is found in polled events:

Parse `payload.message` as a command:
- **"screenshot"** -> take screenshot (Step 4) + reply with visual analysis
- **"go to <place>"** -> find nearest matching waypoint by name, walk there
- **"chat <text>"** -> call `bridge__ui_send_chat` with the provided text
- **"status"** -> reply with current waypoint + player count
- **anything else** -> treat as conversational prompt, generate short in-world chat response

Reply via: `bridge__bridge_nudge(message: "<result summary>")`

---

## Step 8 — Graceful Shutdown

Run when the agent loop completes or the user interrupts.

1. **Unsubscribe all** -- 6 calls to `events_unsubscribe`:
   - `(domain: "party", action: "member_joined")`
   - `(domain: "party", action: "member_left")`
   - `(domain: "party", action: "roster_changed")`
   - `(domain: "ui", action: "chat_received")`
   - `(domain: "bridge", action: "nudge_received")`
   - `(domain: "nav", action: "walk_dispatched")`

2. **Farewell chat**: Call `bridge__ui_send_chat(message: "Signing off -- see you next time!")`

3. **Done** -- no temp files to clean up, no connections to close (MCP server manages its own WebSocket lifecycle).

---

## Quick API Reference

### auth
| Action | Payload | Returns |
|--------|---------|---------|
| `get_status` | -- | `isSignedIn`, `isPerformer`, `username` |
| `get_room_info` | -- | `roomName`, `scenario`, `maxPopulation`, `isLive` |
| `get_room_users` | -- | `localOwnerID`, `users[]`, `totalUsers` |

### world
| Action | Payload | Returns |
|--------|---------|---------|
| `get_world_status` | -- | `loaded`, `loading`, `worldName`, `station` |
| `get_stations` | -- | `stations[]` (name, isLive, isIRL) |
| `load_world` | `station: string` | `message`, `station` |

Events: `world_loaded` {station, worldName}, `world_unloaded` {station}

### nav
| Action | Payload | Returns |
|--------|---------|---------|
| `walk_to` | `destination: [x,y,z]` | `message`, snapped `destination` |
| `get_map` | `refresh?: bool` | `waypoints[]`, `bounds`, `walkabilityGrid` |
| `validate_position` | `position: [x,z]` | `valid`, `snappedPosition`, `distance` |
| `get_position` | -- | `position [x,y,z]`, `ownerID`, `username`, `timestamp` |
| `look_delta` | `x: float` (deg), `y: float` (vertical delta in degrees; positive=zoom out, negative=zoom in) | `yaw: float`, `pitch: float` |
| `set_look` | `yaw: float` (deg), `pitch: float` (deg, -10 to 45; low=zoomed in, high=zoomed out) | `yaw: float`, `pitch: float` |
| `get_look` | -- | `yaw: float`, `pitch: float` |

Events: `walk_dispatched` {destination, performerId}

### vision
| Action | Payload | Returns |
|--------|---------|---------|
| `take_screenshot` | `maxWidth?: int`, `quality?: int (1-100)` | `image` (inline via MCP), `width`, `height`, `sizeBytes`, `estimatedTokens` |

### party
| Action | Payload | Returns |
|--------|---------|---------|
| `get_members` | -- | `localOwnerID`, `members[]`, `totalMembers` |
| `go_to_member` | `ownerID: int` | `position`, `targetOwnerID` |

Events: `member_joined` {username, ownerID, colorIndex, joinTime},
`member_left` {ownerID} (cross-reference with roster to identify who left),
`roster_changed` {localOwnerID, members[], totalMembers}

### ui
| Action | Payload | Returns |
|--------|---------|---------|
| `send_chat` | `message: string`, `targetUsername?: string` | `message: "Chat message dispatched"` |
| `get_chat_mode` | -- | `transport` (public/private), `reach` (global/earshot) |
| `set_chat_transport` | `transport: "public"\|"private"` | confirmation |
| `set_chat_reach` | `reach: "global"\|"earshot"` | confirmation |
| `show_popup` | `title`, `description`, `okText`, `cancelText`, `popupType` | confirmation |
| `select_reaction` | `reactionType: "blurbs"\|"blasts"\|"none"` | confirmation |

Events: `chat_received` {username, message, colorIndex, isLocation, ownerID, targetUsername, isDM}

### bridge
| Action | Payload | Returns |
|--------|---------|---------|
| `nudge` | `message: string` | `message: "Nudge queued"`, `queueDepth` |
| `get_tools` | -- | `tools[]` (full tool schema for all domains) |
| `fake_hit` | -- | `message: "Fake hit dispatched"` -- triggers die animation (auto-resets after 10s) |

Events: `nudge_received` {message, senderClientId}
