# 09 — Storage-Backed Edits, File History, Projects & Git Integration

Date: 2026-03-22

This entry captures a layered roadmap where each tier builds on the previous one.
The layers are designed to be independently useful — you don't need projects to
have file history, and you don't need git to have checkpoints.

---

## Layer 0: Storage-Backed Add Buffer

**Problem.** The PieceTable's add buffer is an in-memory `StringBuilder`. Any
operation that touches most of a large file (tab conversion, replace-all,
normalize whitespace) materializes a huge string. For a 500 MB file, that's
500 MB in the add buffer alone.

**Insight.** The PieceTable already treats the add buffer as opaque via
`BufFor()` / `VisitPieces` / `ForEachPiece`. Pieces reference (buffer, offset,
length) — they don't care whether the buffer lives in memory or on disk.

**Design.** The add buffer becomes a sequence of *segments*:

- **Small segments** (below a configurable threshold, e.g. 1 MB) stay in memory
  as today — a `StringBuilder` or `char[]`.
- **Large segments** are flushed to a temp file. The piece table holds a
  reference (segment ID + offset + length) instead of a string.

The `IBuffer` abstraction already supports this: `CopyTo` and the indexer can
read from a memory-mapped file or a `FileStream` seek+read.

**Undo model.** Currently, `DeleteEdit` captures pieces via `CapturePieces()`.
These are lightweight descriptors (buffer ID, offset, length). If those pieces
reference on-disk segments, undo is just restoring piece pointers — no data is
copied. A tab conversion that produces 500 MB writes to a temp segment; undo
swaps back to the old pieces pointing at the original base file.

**Key property:** segments are append-only and immutable once written. No
deduplication needed (unlike git's content-addressable store). This keeps the
implementation simple.

### Implementation sketch

```
SegmentStore
  ├── MemorySegment   (id, char[], offset, length)
  ├── FileSegment     (id, path, offset, length)  ← memory-mapped or seekable
  └── SpillThreshold  (configurable, e.g. 1 MB)

AddBuffer
  ├── current: MemorySegment  (active append target)
  ├── segments: List<Segment> (completed segments)
  └── Append(text) → if current exceeds threshold, flush to FileSegment
                     and start a new MemorySegment

PieceTable.BufFor(piece)
  → if piece.IsOriginal → base IBuffer (unchanged)
  → if piece.IsAdd      → segments[piece.SegmentId].Read(piece.Offset, piece.Length)
```

The temp directory for segments can live under `%APPDATA%/DMEdit/segments/`
or a per-session temp folder.

---

## Layer 1: File History & Checkpoints

**Builds on:** Layer 0 (segment store)

**Concept.** Each document has an append-only *history log* — a sequence of
snapshots of the piece table state. This is analogous to git's commit log but
scoped to a single file and fully automatic.

**Automatic snapshots.** Taken on save, before large operations, and periodically
(e.g. every N edits or M minutes). Lightweight — just the list of piece
descriptors plus metadata (timestamp, optional summary).

**Named checkpoints.** The user can explicitly checkpoint with an optional
description. Could also be auto-summarized by AI ("Refactored the authentication
handler to use middleware").

**Navigable history.** A UI panel showing the checkpoint log with timestamps and
summaries. Selecting a checkpoint shows a read-only view of the document at that
point. Diff between any two checkpoints.

**Timeline / blame.** For each line in the current document, trace back through
the history log to find which checkpoint introduced it. Analogous to `git blame`
but using the internal history.

**Storage.** The history log is a file alongside (or inside) the segment store.
Since segments are immutable, old snapshots remain valid — their piece
descriptors still point at valid segment data. Garbage collection reclaims
segments no longer referenced by any snapshot.

### Data model sketch

```
Snapshot
  ├── id: uint64 (monotonic)
  ├── timestamp: DateTimeOffset
  ├── summary: string?           ← user or AI-generated
  ├── pieces: PieceDescriptor[]  ← the full piece table state
  ├── selection: Selection       ← caret/selection at snapshot time
  └── metadata: LineEndingInfo, IndentInfo, EncodingInfo

HistoryLog
  ├── snapshots: List<Snapshot>
  ├── AddSnapshot(pieces, summary?)
  ├── GetSnapshot(id) → Snapshot
  └── Diff(from, to) → list of changes
```

---

## Layer 2: Projects (Multi-File Scope)

**Builds on:** Layer 1 (per-file history)

**Concept.** A *project* is a directory of related files managed as a unit.
Opening a folder creates a project context that enables cross-file operations.

**Features unlocked:**
- **Cross-file search and replace** — same as single-file replace-all but
  applied across the project. Each file gets its own edit via Layer 0.
- **Project-scoped checkpoints** — a checkpoint that captures the state of all
  open/modified files simultaneously. Analogous to a git commit spanning
  multiple files.
- **File tree panel** — browse and open files from the project directory.
- **Embedded data types** — images and other non-text assets can live as files
  in the project directory. When editing Markdown, image references resolve to
  project files.

**Markdown + images.** With project context, `![alt](image.png)` can display
the actual image inline in the WYSIWYG block model. Inline SVG blocks
(`<svg>...</svg>` or fenced code blocks with svg type) can render directly —
this isn't standard Markdown but is a natural extension for a WYSIWYG editor
that owns its renderer.

**Project storage.** A `.dmedit/` directory (hidden) in the project root,
containing:
- Segment store (shared across all project files)
- History logs (one per file, plus a project-level log for multi-file
  checkpoints)
- Project settings (editor preferences scoped to this project)

### Data model sketch

```
Project
  ├── root: DirectoryPath
  ├── files: Dictionary<RelativePath, FileState>
  ├── history: ProjectHistoryLog  ← multi-file checkpoint log
  └── settings: ProjectSettings

ProjectCheckpoint
  ├── id: uint64
  ├── timestamp: DateTimeOffset
  ├── summary: string?
  └── files: Dictionary<RelativePath, SnapshotId>  ← per-file snapshot refs
```

---

## Layer 3: Git Integration

**Builds on:** Layer 2 (projects)

**Separate concern.** Git integration is documented separately (see below) to
keep the core dmedit design clean. The internal history system (Layers 0-2) is
the primary storage model. Git is an optional overlay that adds collaboration
and remote sync.

**Transition path:**
1. **No git** — files use internal history only. Most users stay here.
2. **Initialize git** — the `.dmedit/` history can be exported as git commits
   (each project checkpoint becomes a git commit). From this point forward,
   saves can also create git commits.
3. **Existing git repo** — if the project directory is already a git repo,
   dmedit detects it and offers git-aware features alongside internal history.

**Git-aware features (when git is active):**
- Status indicators on files (modified, staged, untracked)
- Commit from within the editor (checkpoint = commit)
- Push/pull to/from remotes
- Branch switching
- Diff view against any git commit
- Blame using git history (supplements internal blame)

**Implementation.** Use libgit2sharp (or shell out to `git` CLI) rather than
reimplementing git internals. The internal history and git coexist — internal
history provides fine-grained undo/redo between git commits.

---

## Git Integration — Design Notes

See [`../Thoughts on Git.md`](../Thoughts%20on%20Git.md) for a separate scratch
pad exploring git integration ideas, open questions, and research topics. Kept
outside the design journal to avoid coupling the core dmedit storage model to
git's data model.

---

## Relationship to Existing Deferred Items

| Design journal item | Addressed by |
|---|---|
| Storage-backed large edits | Layer 0 — segment store |
| Guard against whole-document string materialization | Layer 0 — large edits write to disk segments |
| Delayed clipboard rendering | Orthogonal — still needed for clipboard, but Layer 0 solves the edit side |
| Block model / WYSIWYG | Layer 2 projects + embedded images enhance this |
| Windows installer (Velopack) | Independent |
