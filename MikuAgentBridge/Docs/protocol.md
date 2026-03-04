# MikuAgentBridge v1 Protocol

Transport:
- Named pipe: `miku_agent_bridge_v1`
- Duplex
- Message-based pipe stream
- Frame: 4-byte little-endian length prefix + UTF-8 JSON payload

Auth:
- Token file: `%AppData%\\MikuAgentBridge\\secret.token`
- Every request must include `token`

## Request

```json
{
  "id": "b7b2c2c4-3b2b-4f5f-8f64-1f7a7a3d6a4e",
  "token": "base64-token",
  "cmd": "status.get",
  "args": {}
}
```

## Response

```json
{
  "id": "b7b2c2c4-3b2b-4f5f-8f64-1f7a7a3d6a4e",
  "ok": true,
  "data": {},
  "error": null
}
```

## Commands

### `status.get`
Returns:
- `modVersion`
- `desktopMatePid`
- `melonLoaderVersion`
- `characterFound`
- `characterName`
- `mischiefEnabled`
- `autoOffRemainingSeconds`
- `allowList`
- `captureEnabled`
- `captureMinIntervalMs`
- `lastError`

### `screen.capture`
Args:
- `target`: `virtual_desktop`
- `format`: `png`
- `return`: `path` (default) or `base64`

Safety:
- Requires `CaptureEnabled=true`
- Enforces `CaptureMinIntervalMs`

Returns:
- `return`: `path` or `base64`
- `path` when capture is stored to disk
- `base64` when inline payload is under cap
- `fallbackToPath=true` when `return=base64` exceeds cap

Capture path:
- `%LocalAppData%\\MikuAgentBridge\\captures\\capture_yyyyMMdd_HHmmss_fff.png`

### `window.nudge`
Args:
- `target`: `active`
- `dx`: int
- `dy`: int
- `animate`: bool (default `true`)

Safety:
- Requires `MischiefGate` enabled
- Requires foreground process in `AllowedProcessesForMischief`
- Hard deny list blocks by default: `taskmgr`, `processhacker`, `msconfig`, `regedit`
- Final window position is clamped to virtual desktop bounds

Returns:
- target and process metadata
- original and final position
- window size
- whether animation trigger was requested
