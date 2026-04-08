# `App`

`src/DMEdit.App/App.axaml.cs` (34 lines)

Avalonia `Application` subclass. Wires up the main window and
single-instance listener.

## Likely untested
- Trivial.

## Architectural concerns
- **Single-instance wiring lives here** — the handoff
  (`Program.SingleInstance.FileRequested`) is connected
  after `MainWindow` is constructed. Race between
  launch and first-instance signal possible.
