# `EncodingInfo` / `FileEncoding` enum

`src/DMEdit.Core/Documents/EncodingInfo.cs`
Tests: none direct. Exercised indirectly via `PagedFileBufferTests`
BOM detection tests.

## Likely untested

- **No dedicated test file.**
- `Label` — six values to match against; no direct assertions.
- `Default` — trivial.
- **`GetDotNetEncoding`** — six branches returning appropriate
  `System.Text.Encoding` instances. The `Ascii` branch returns
  `Encoding.ASCII` which is a shared singleton; the others
  `new` up a fresh instance each call. Inconsistent. The UTF-8
  and UTF-16 branches allocate a new encoding on every
  `GetDotNetEncoding` call, which can be frequent during save.
  A static cache per value would fix it.
- **`GetPreamble`** — three non-null branches (Utf8Bom, Utf16Le,
  Utf16Be), null for others. Not asserted directly.
- **`FromDetection`** — six branches, several of which are hit by
  BOM detection tests but none directly. Specifically untested:
  - `Windows1252` (code page 1252 detection)
  - `Ascii` (code page 20127)
  - Fallback branch for unknown code pages
- **`FromDetection` with a `UTF8Encoding` that has `Preamble`
  set to empty** — the `encoding is UTF8Encoding` check is
  independent of BOM state. Works because the `hadBom` flag is
  passed separately.

## Architectural concerns

- **`Utf8Bom` / `Utf16Le` / `Utf16Be` imply a BOM, but
  `EncodingInfo.FromDetection(UTF8Encoding, hadBom: false)`
  returns `Utf8`. So the `Utf8Bom` value can only originate from
  detection. If the user later toggles "add BOM" in settings,
  the `Utf8Bom` → `Utf8` transition must go through
  `GetDotNetEncoding` which builds the right encoder. Works.
- **No validation** of the enum value in `FromDetection`'s
  fallback branch — it silently returns Default. Arguably
  correct (UTF-8 is the safe default) but a log line would help.
- **`FileEncoding.Unknown`** — meant for the loading state but
  most getters silently fall through to UTF-8. Document the
  invariant "Unknown may appear during load but never in a
  saved settings file."

## Simplification opportunities

- **Cache encoding instances** to avoid per-call allocation.
  Static readonly fields for each of the six, returned from
  `GetDotNetEncoding`.
- **Consider merging `FileEncoding` and `EncodingInfo`** — the
  record struct only holds the enum. `public enum FileEncoding`
  alone with extension methods would do. Kept as a record
  struct for consistency with `LineEndingInfo`/`IndentInfo`
  which carry more data.
