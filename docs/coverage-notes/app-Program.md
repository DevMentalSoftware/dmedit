# `Program`

`src/DMEdit.App/Program.cs` (289 lines)

Application entry point. Velopack bootstrap, Encoding provider
registration, single-instance check, global exception handlers,
Avalonia start, shell integration registration.

## Likely untested

- **Velopack bootstrap branches** — first run, post-install,
  pre-uninstall.
- **Single-instance hand-off** — 2nd instance sends file and
  exits.
- **Fatal exception handler** — re-entrance guard, crash
  report write, dialog display.
- **Per journal "non-responsive after crash"**: open item.
  Several hypotheses not yet diagnosed (hung zombie, vpk.Run
  wedging, Avalonia init hang, etc.). The journal proposes:
  1. Startup breadcrumb log to `%AppData%/DMEdit/startup.log`
  2. Hard watchdog in `HandleFatalException` (Task.Delay 10s
     then `Environment.FailFast`)
- **`RegisterShellIntegration` / `UnregisterShellIntegration`**
  — Windows-only file-type assoc. Not tested.

## Architectural concerns

- **Critical startup logic with many side effects** — Velopack,
  encoding provider, single-instance, exception handlers,
  Avalonia build. Any one of them can hang the process.
- **No startup breadcrumb log** (per journal open item). A
  future crash report would benefit from knowing how far
  startup got.
- **Dispatcher exception handler installed `AfterSetup`** — a
  crash before that point propagates to Avalonia's default
  handler. Small window but real.
- **`_handlingFatal` static int guard** — sequence of fatal
  exceptions in different threads would produce
  interleaving. The guard uses `Interlocked` presumably.
- **Hard watchdog missing** — the dialog flow itself can
  hang (e.g. `done.Wait()` on a `ManualResetEventSlim`
  nobody sets). Watchdog is scoped but not shipped.
