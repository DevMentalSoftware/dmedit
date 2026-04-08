# Coverage Notes (temporary)

Per-class analysis generated 2026-04-08. One file per class/file, organized
by source directory. Each file captures:

- **Likely untested** — observable behavior not obviously covered by the test
  suite. Not a substitute for a real coverage tool; these are heuristic flags
  based on reading tests and source side-by-side.
- **Architectural concerns** — smells, tight coupling, layering violations,
  missing abstractions.
- **Simplification opportunities** — dead code, redundant state, overly
  general helpers used in one place, places where a newer language feature
  would shrink the file.
- **Bugs / hazards** — anything that looks broken on a read-through.

These files are scratch output, not design docs. Process one at a time later,
delete after the fix lands. `INDEX.md` links them all in one place.

Test baseline when these notes were written: 657 tests
(521 Core + 47 Rendering + 89 App, 1 skipped).
