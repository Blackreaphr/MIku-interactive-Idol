# Companion v1 Foundation

Companion is a Windows desktop overlay foundation built on WPF with:

- Transparent always-on-top overlay window.
- Click-through outside the character hitbox.
- Tray controls for overlay, mischief state, capture, settings, and exit.
- Global kill hotkey (`Ctrl+Alt+Shift+F12` by default).
- Rate-limited virtual desktop screenshot capture.
- Local named-pipe control API with per-user token authentication.

## Solution Layout

- `Companion.App` - WPF app, overlay, tray, screenshot service, named-pipe server.
- `Companion.Core` - safety gate, settings, limiter, persistence, token, logging.
- `Companion.Native` - Win32 interop and hotkey registration manager.
- `Companion.Tests` - unit/integration tests for core and pipe behavior.

## Build and Test

```powershell
dotnet restore Companion.sln
dotnet build Companion.sln -c Debug
dotnet test Companion.Tests\Companion.Tests.csproj -c Debug
```

## Run

```powershell
dotnet run --project Companion.App\Companion.App.csproj
```

## Runtime Files

Companion stores runtime data under:

`%LocalAppData%\Companion`

- `settings.json` - persisted app settings.
- `token.txt` - local API auth token.
- `logs\companion-YYYY-MM-DD.log` - structured JSONL logs.
- `captures\capture-*.png` - tray-triggered screenshots.

## Named Pipe API

- Pipe name: `companion.control.v1.<SID>`
- Frame format: UTF-8 JSON, one object per line.
- Request fields: `id`, `token`, `method`, optional `params`.
- Response fields: `id`, `ok`, `result` or `error`.

Supported methods:

1. `ping`
2. `get_status`
3. `set_mischief_enabled`
4. `force_off`
5. `capture_virtual_desktop_png`
6. `mischief.cursor_grab_once`
7. `windows.list`
8. `windows.active`
9. `windows.under_cursor`
10. `window.focus`
11. `window.move_resize`
12. `window.nudge`
13. `window.minimize`
14. `window.maximize`
15. `window.restore`
16. `window.close_request`
17. `get_settings`
18. `update_settings`

`mischief.cursor_grab_once` accepts optional `durationMs` and `rectSizePx`. Per-call values can only tighten configured limits (they cannot exceed configured settings). The action is gated by `MischiefGate`, `cursorGrabEnabled`, cooldown/active state, and foreground-process allowlist policy.

`update_settings` accepts:

- `allowedProcessesForMischief` (array of process tokens). Entries are normalized to lowercase names without `.exe`. An empty allowlist means mischief window actions are allowed for all processes.
- `cursorGrabEnabled` (bool, default `false`)
- `cursorGrabDurationMs` (int, `1..1500`, default `600`)
- `cursorGrabCooldownMs` (int, `0..60000`, default `5000`)
- `cursorGrabRectSizePx` (int, `4..200`, default `24`)
- `cursorGrabRequireAllowList` (bool, default `true`)
- `allowedProcessesForCursorGrab` (array of process tokens, normalized like other allowlists)

When `cursorGrabRequireAllowList` is true and `allowedProcessesForCursorGrab` is empty, cursor grabs are denied.

`get_status` now includes cursor-grab state/config fields:
`cursorGrabEnabled`, `cursorGrabDurationMs`, `cursorGrabCooldownMs`, `cursorGrabRectSizePx`, `cursorGrabRequireAllowList`, `allowedProcessesForCursorGrab`, and `cursorGrabActive`.

## Pipe Client Example (PowerShell)

```powershell
$token = Get-Content "$env:LOCALAPPDATA\Companion\token.txt" -Raw
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipeName = "companion.control.v1.$sid"

$client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
$client.Connect(3000)

$writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false), 1024, $true)
$writer.AutoFlush = $true
$reader = [System.IO.StreamReader]::new($client, [System.Text.Encoding]::UTF8, $false, 1024, $true)

$req = @{
  id = "1"
  token = $token.Trim()
  method = "get_status"
  params = @{}
} | ConvertTo-Json -Compress

$writer.WriteLine($req)
$response = $reader.ReadLine()
$response

$reader.Dispose()
$writer.Dispose()
$client.Dispose()
```

## Troubleshooting

- If the hotkey fails to register, another process likely owns that combo. Change hotkey in settings.
- If capture fails with rate limit, wait for `retryAfterMs` or increase capture interval in settings.
- If pipe calls fail with `unauthorized`, verify token from `%LocalAppData%\Companion\token.txt`.
