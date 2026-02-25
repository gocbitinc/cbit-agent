# Phase 6 — Remote Terminal

## Overview

Add real-time remote CMD and PowerShell terminal sessions from the web UI to any online agent. The backend acts as a relay between the browser (WebSocket) and the agent (WebSocket). All commands are logged for audit. This phase also adds the WebSocket listener on the agent side.

## Prerequisites

- Phase 5 complete and passing validation checklist (agents checking in, assets in database)

---

## Step 1: Database Migration

### Create migration 005_terminal.sql:

```sql
CREATE TABLE terminal_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  asset_id UUID NOT NULL REFERENCES assets(id),
  user_id UUID NOT NULL REFERENCES users(id),
  shell_type VARCHAR(15) CHECK (shell_type IN ('cmd', 'powershell')),
  started_at TIMESTAMPTZ DEFAULT NOW(),
  ended_at TIMESTAMPTZ,
  status VARCHAR(15) CHECK (status IN ('active', 'closed', 'disconnected'))
);

CREATE INDEX idx_terminal_sessions_asset ON terminal_sessions(asset_id);
CREATE INDEX idx_terminal_sessions_user ON terminal_sessions(user_id);

CREATE TABLE terminal_command_log (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES terminal_sessions(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES users(id),
  command TEXT NOT NULL,
  output TEXT,
  executed_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_command_log_session ON terminal_command_log(session_id);
```

Run the migration.

---

## Step 2: WebSocket Server — Backend

### Create src/websocket/index.js

Set up a WebSocket server alongside the existing Express HTTP server. Use the `ws` npm package.

```bash
npm install ws
```

**Architecture:**

The backend maintains TWO pools of WebSocket connections:
1. **Agent connections:** Agents connect and stay connected persistently. Keyed by agent_id.
2. **Tech browser connections:** Tech browsers connect when opening a terminal tab. Keyed by session_id.

The backend relays messages between paired tech and agent connections for a given terminal session.

```
[Browser/Tech] ←—WSS—→ [Backend Relay] ←—WSS—→ [Agent]
```

### WebSocket setup in app.js:

```javascript
const { WebSocketServer } = require('ws');
const http = require('http');

const server = http.createServer(app);
const wss = new WebSocketServer({ server });

// Pass wss to the websocket handler module
require('./websocket')(wss);

server.listen(PORT);
```

### WebSocket handler (src/websocket/index.js):

**Connection authentication:**

On connection, the client sends an initial message identifying itself:

```json
// Agent connecting:
{ "type": "agent_auth", "agent_id": "xxx", "agent_token": "xxx" }

// Tech browser connecting:
{ "type": "tech_auth", "access_token": "xxx" }
```

Validate the token. If invalid, close the connection with code 4001.

**Agent connection pool:**

```javascript
const agentConnections = new Map(); // agent_id → WebSocket

// When agent authenticates:
agentConnections.set(agentId, ws);

// On close:
agentConnections.delete(agentId);
```

**Tech session pool:**

```javascript
const techSessions = new Map(); // session_id → { techWs, agentId }
```

**Message routing:**

After authentication, messages are routed based on type:

From tech browser:
```json
{ "type": "terminal_start", "asset_id": "uuid", "shell_type": "powershell" }
{ "type": "terminal_input", "session_id": "uuid", "data": "Get-Process\r\n" }
{ "type": "terminal_resize", "session_id": "uuid", "cols": 120, "rows": 30 }
{ "type": "terminal_stop", "session_id": "uuid" }
```

From agent:
```json
{ "type": "terminal_started", "session_id": "uuid" }
{ "type": "terminal_output", "session_id": "uuid", "data": "output text..." }
{ "type": "terminal_error", "session_id": "uuid", "error": "reason" }
{ "type": "terminal_exited", "session_id": "uuid", "exit_code": 0 }
```

---

## Step 3: Terminal Session Flow — Backend

### When tech sends "terminal_start":

1. Validate the asset exists and belongs to an accessible customer
2. Look up the agent connection for this asset's agent_id in agentConnections
3. If agent not connected, return error: `{ "type": "terminal_error", "error": "Agent is offline" }`
4. Create a terminal_sessions record in the database:
   ```
   asset_id, user_id (from tech auth), shell_type, status = 'active'
   ```
5. Store the session mapping: techSessions.set(session_id, { techWs, agentId, userId })
6. Forward to agent:
   ```json
   { "type": "terminal_start", "session_id": "uuid", "shell_type": "powershell" }
   ```
7. Send confirmation back to tech:
   ```json
   { "type": "terminal_session_created", "session_id": "uuid" }
   ```

### When tech sends "terminal_input":

1. Look up session in techSessions
2. Forward to the agent connection:
   ```json
   { "type": "terminal_input", "session_id": "uuid", "data": "command text\r\n" }
   ```
3. Log the command to terminal_command_log (extract command from data — strip \r\n)
4. Do NOT wait for output to log — output comes asynchronously

### When agent sends "terminal_output":

1. Look up which tech session this belongs to
2. Forward to the tech's browser WebSocket:
   ```json
   { "type": "terminal_output", "session_id": "uuid", "data": "output..." }
   ```
3. Optionally log output to terminal_command_log (append to the most recent command's output, or create a new log entry). Output logging can be truncated if very large — keep last 10KB per command.

### When tech sends "terminal_stop":

1. Forward to agent: `{ "type": "terminal_stop", "session_id": "uuid" }`
2. Update terminal_sessions: status = 'closed', ended_at = NOW()
3. Clean up techSessions map

### When tech disconnects (WebSocket close):

1. Find all active sessions for this tech
2. For each, send "terminal_stop" to the agent
3. Update sessions to status = 'disconnected', ended_at = NOW()
4. Clean up maps

### When agent disconnects:

1. Remove from agentConnections
2. Find all active sessions involving this agent
3. Send error to each tech: `{ "type": "terminal_error", "session_id": "...", "error": "Agent disconnected" }`
4. Update sessions to status = 'disconnected'
5. Clean up maps

### When tech sends "terminal_resize":

1. Forward to agent: `{ "type": "terminal_resize", "session_id": "uuid", "cols": 120, "rows": 30 }`
2. Agent adjusts the pseudo-console size

---

## Step 4: Terminal Session Flow — Agent (C#)

### Add WebSocket client to the agent

Add NuGet package or use built-in System.Net.WebSockets.ClientWebSocket.

### Create Services/WebSocketClient.cs:

**Connection:**
- Connect to WSS endpoint: `wss://axis.gocbit.com/api/agent/ws`
- Send auth message: `{ "type": "agent_auth", "agent_id": "xxx", "agent_token": "xxx" }`
- Maintain persistent connection with auto-reconnect on disconnect

**Reconnection logic:**
- On disconnect: wait 5 seconds, attempt reconnect
- Exponential backoff: 5s, 10s, 20s, 40s, max 60s
- Reset backoff on successful connection
- Log reconnection attempts

**Message handling:**

On receiving "terminal_start":
1. Start a new shell process based on shell_type:
   ```csharp
   var process = new Process();
   process.StartInfo.FileName = shellType == "powershell" ? "powershell.exe" : "cmd.exe";
   process.StartInfo.UseShellExecute = false;
   process.StartInfo.RedirectStandardInput = true;
   process.StartInfo.RedirectStandardOutput = true;
   process.StartInfo.RedirectStandardError = true;
   process.StartInfo.CreateNoWindow = true;
   ```
   For PowerShell, use arguments: `-NoLogo -NoProfile -NonInteractive` but keep it interactive for the terminal experience. Actually, just use `-NoLogo` to suppress the banner.
2. Store the process keyed by session_id
3. Start reading stdout and stderr asynchronously
4. Send "terminal_started" back to server
5. Pipe stdout/stderr output to WebSocket as "terminal_output" messages

On receiving "terminal_input":
1. Look up the process by session_id
2. Write data to process.StandardInput
3. The output will come back asynchronously through the stdout/stderr readers

On receiving "terminal_resize":
1. If using ConPTY (pseudo-console), resize it
2. For basic Process redirect, resize is a best-effort operation

On receiving "terminal_stop":
1. Kill the process if still running
2. Clean up resources
3. Send "terminal_exited" with exit code

### Better approach — ConPTY (Windows Pseudo Console):

For a proper terminal experience with full interactive support (tab completion, arrow keys, colors), use the Windows Pseudo Console API (ConPTY) available in Windows 10 1809+.

```csharp
// PseudoConsole approach gives true terminal behavior
// Uses CreatePseudoConsole Win32 API
// Provides a pipe pair for reading/writing
// Supports ANSI escape codes, colors, cursor movement
```

If ConPTY is too complex for v1, the basic Process redirect approach works for simple commands. ConPTY can be added as an enhancement. Note which approach is used so the frontend terminal emulator can be configured accordingly (raw mode vs line mode).

### Track active sessions:

```csharp
private readonly Dictionary<string, Process> _activeSessions = new();
```

Clean up on agent shutdown: kill all active processes.

---

## Step 5: Frontend — Terminal Component

### Install xterm.js:

```bash
npm install @xterm/xterm @xterm/addon-fit @xterm/addon-web-links
```

### Create src/components/Terminal/TerminalComponent.jsx:

**Props:**
- assetId: UUID of the asset
- assetName: for display
- onClose: callback when terminal is closed

**Component lifecycle:**

1. On mount:
   - Create xterm.js Terminal instance with dark theme
   - Open WebSocket connection to backend: `wss://axis.gocbit.com/api/agent/ws` (or relative wss:// path)
   - Send tech auth: `{ "type": "tech_auth", "access_token": "xxx" }`
   - On auth confirmed, send: `{ "type": "terminal_start", "asset_id": "xxx", "shell_type": "powershell" }`

2. On "terminal_session_created" message:
   - Store session_id
   - Display "Connecting to {assetName}..." in terminal

3. On "terminal_started" message:
   - Display "Connected. Shell ready." in terminal
   - Focus the terminal

4. On "terminal_output" message:
   - Write data to xterm: `terminal.write(data)`

5. On xterm user input (terminal.onData):
   - Send to backend: `{ "type": "terminal_input", "session_id": "xxx", "data": "user input" }`

6. On xterm resize (terminal.onResize via fit addon):
   - Send: `{ "type": "terminal_resize", "session_id": "xxx", "cols": x, "rows": y }`

7. On "terminal_error" message:
   - Display error in terminal in red
   - Show reconnect option if appropriate

8. On unmount / close:
   - Send: `{ "type": "terminal_stop", "session_id": "xxx" }`
   - Close WebSocket
   - Dispose xterm instance

**xterm.js configuration:**

```javascript
const terminal = new Terminal({
  theme: {
    background: '#1e1e1e',
    foreground: '#d4d4d4',
    cursor: '#d4d4d4',
    cursorAccent: '#1e1e1e',
    selectionBackground: '#264f78',
    black: '#1e1e1e',
    red: '#f44747',
    green: '#6a9955',
    yellow: '#d7ba7d',
    blue: '#569cd6',
    magenta: '#c586c0',
    cyan: '#4ec9b0',
    white: '#d4d4d4',
  },
  fontFamily: "'Cascadia Code', 'Consolas', 'Courier New', monospace",
  fontSize: 14,
  cursorBlink: true,
  cursorStyle: 'block',
  scrollback: 5000,
  convertEol: true,
});

// Fit addon to auto-resize to container
const fitAddon = new FitAddon();
terminal.loadAddon(fitAddon);

// Web links addon for clickable URLs
const webLinksAddon = new WebLinksAddon();
terminal.loadAddon(webLinksAddon);
```

---

## Step 6: Frontend — Terminal Tab on Asset Page

### Update src/pages/AssetDetail.jsx:

**Terminal tab (replace placeholder):**

- Shell type selector: two buttons "CMD" and "PowerShell" (PowerShell selected by default)
- "New Session" button
- Tab bar for multiple concurrent sessions (each is a TerminalComponent instance)
- Each tab: shell type icon, session label ("PowerShell #1"), close button (X)
- Active tab shows the terminal, inactive tabs maintain their connection in the background
- "Disconnect All" button

**Layout:**
- Terminal takes up the full main content area when the Terminal tab is active
- Dark background for the entire tab area to match terminal theme
- Terminal container should resize with the window (use ResizeObserver + fit addon)

**Connection status indicator:**
- Green dot + "Connected" when WebSocket is active and session is running
- Yellow dot + "Connecting..." during setup
- Red dot + "Disconnected" if connection lost
- "Reconnect" button on disconnect

**Disabled state:**
- If asset is offline (is_online = false), show "Asset is offline — terminal unavailable" instead of the terminal
- Terminal button in the top bar should also be disabled with tooltip

---

## Step 7: Frontend — Top Bar Terminal Button

### Update AssetDetail.jsx top bar:

The "Terminal" button in the asset page top bar should:
- If asset is online: click switches to the Terminal tab and auto-creates a new PowerShell session
- If asset is offline: disabled with tooltip "Asset is offline"

---

## Step 8: Terminal Session History

### API endpoint:

**GET /api/assets/:id/terminal-sessions**
- Requires auth
- Returns recent terminal sessions for this asset
- Each: session_id, user name, shell type, started_at, ended_at, status, command_count
- Sorted by started_at DESC
- Paginated, default limit 20

**GET /api/terminal-sessions/:sessionId/log**
- Requires auth
- Returns command log for a session
- Each: command, output (truncated to 1KB), executed_at
- Sorted by executed_at ASC

### Frontend — Session History section on Terminal tab:

Below the active terminal area (or as a sub-tab):
- "Session History" collapsible section
- Table: User, Shell Type, Started, Duration, Commands, Status
- Click to expand and view command log
- Read-only — just for audit review

---

## Step 9: Agent WebSocket Integration with Main Worker

### Update Worker.cs:

The agent's main Worker service needs to manage both:
1. The periodic HTTP check-in loop (existing from Phase 5)
2. The persistent WebSocket connection (new)

**Startup sequence:**
1. Load config, register if needed (existing)
2. Start WebSocket connection in background task
3. Start check-in timer (existing)

**WebSocket and check-in are independent:**
- Check-in continues via HTTP REST on the timer
- WebSocket maintains a persistent connection for real-time commands (terminal)
- If WebSocket disconnects, check-in continues unaffected
- If server is unreachable for check-in, WebSocket reconnect also backs off

**Thread safety:**
- Terminal sessions run shell processes in their own threads
- Multiple concurrent sessions are supported (use ConcurrentDictionary for session tracking)
- WebSocket message handling is async

---

## Step 10: Security Considerations

### Command logging:

- ALL commands sent through the terminal are logged with the tech's user ID and timestamp
- This creates an audit trail for compliance and troubleshooting
- Output is logged (truncated for large outputs) so admins can review what was done

### Session limits:

- Maximum 3 concurrent terminal sessions per asset (prevent resource exhaustion)
- Maximum 10 concurrent terminal sessions per tech (prevent abuse)
- Sessions auto-timeout after 30 minutes of inactivity (no input from tech)
- On timeout: send terminal_stop to agent, close session, notify tech

### Input sanitization:

- The terminal is a raw shell — there is no command filtering or blocking in v1
- The audit log is the control mechanism
- Future enhancement: configurable command blocklist

---

## Step 11: WebSocket Health and Reconnection

### Agent-side keepalive:

- Send ping frames every 30 seconds to keep the connection alive through proxies/NAT
- If no pong received within 10 seconds, consider connection dead and reconnect
- Log connection state changes

### Server-side keepalive:

- Send ping to each connected agent every 30 seconds
- If no pong within 10 seconds, close the connection and clean up
- Remove from agentConnections map

### Frontend keepalive:

- Browser WebSocket sends ping every 30 seconds
- Display connection status indicator
- On disconnect: attempt reconnect with backoff, show status to tech
- If reconnect succeeds during an active terminal session, the session is lost — tech needs to start a new one (shell process on agent was killed on disconnect)

---

## Validation Checklist

After completing Phase 6, verify:

**Backend:**
- [ ] Migration creates terminal_sessions and terminal_command_log tables
- [ ] WebSocket server starts alongside Express HTTP server
- [ ] Agent WebSocket connections authenticate and are tracked
- [ ] Tech browser WebSocket connections authenticate and are tracked
- [ ] terminal_start creates a session record and relays to agent
- [ ] terminal_input relays from tech to agent
- [ ] terminal_output relays from agent to tech
- [ ] terminal_stop cleans up session on both sides
- [ ] Commands are logged to terminal_command_log
- [ ] Agent disconnect notifies connected techs
- [ ] Tech disconnect sends stop to agent
- [ ] Session timeout after 30 minutes inactivity works

**Agent (C#):**
- [ ] Agent establishes WebSocket connection on startup
- [ ] Agent reconnects on WebSocket disconnect with backoff
- [ ] Agent spawns cmd.exe or powershell.exe on terminal_start
- [ ] stdin/stdout/stderr piped correctly over WebSocket
- [ ] Interactive commands work (dir, Get-Process, etc.)
- [ ] Agent handles terminal_stop by killing the process
- [ ] Multiple concurrent sessions work
- [ ] Agent handles terminal_resize messages
- [ ] Shell processes are cleaned up on agent shutdown

**Frontend:**
- [ ] Terminal tab appears on asset detail page
- [ ] Shell type selector (CMD / PowerShell) works
- [ ] Clicking "New Session" opens a terminal and connects
- [ ] xterm.js renders with dark theme and correct fonts
- [ ] Typing commands sends input and displays output
- [ ] Terminal resizes with the window
- [ ] Multiple tabs for concurrent sessions work
- [ ] Close button on tab disconnects that session
- [ ] Connection status indicator shows correct state
- [ ] "Asset is offline" message shown when asset is not online
- [ ] Terminal button in top bar works (switches to tab, creates session)
- [ ] Session history shows past sessions with command logs
- [ ] Disconnect/reconnect scenarios handled gracefully
- [ ] Copy/paste works in the terminal
