# `LinuxClipboardService`

`src/DMEdit.App/Services/LinuxClipboardService.cs` (174 lines)

Process-based clipboard impl for Linux. Spawns `wl-copy`/
`wl-paste` or `xclip` to read/write the clipboard. Used
because Avalonia's clipboard has gaps on Linux.

## Likely untested

- **Whole class** — no tests.
- **Wayland vs X11 detection** — which tool is chosen.
- **UTF-8 round-trip of special characters** through
  stdin/stdout process pipes.
- **Stream-based paste** — `AppendUtf8` into the add buffer
  via process stdout. Per entry 12, this is a real code
  path.
- **Cancel during a large paste.**
- **Tool not installed** — should degrade gracefully.
- **Binary clipboard content** — filtered out? Or passed
  through as garbage?

## Architectural concerns

- **Implements `INativeClipboardService`** — the interface's
  `PasteToStream` default throws; this class must override.
- **Spawning processes per op** — latency. Caching a long-
  lived `wl-paste --watch` would be faster but complex.
- **No clipboard-change notification** — Linux clipboards
  historically require a watcher process. Missing means no
  "paste enabled" live UI state.
