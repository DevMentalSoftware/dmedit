# Thoughts on Git

Scratch pad for ideas about git integration in dmedit. This is a thinking
space, not a design document. Ideas here may or may not make it into the
actual design (see `design-journal/09-storage-and-history.md` Layer 3).

---

## Why git matters for dmedit

Git is the dominant version control system for software. Many dmedit users
will already have git repos. Rather than ignore git or treat it as an external
tool, dmedit could provide a seamless experience where:

- Non-technical users get version history without knowing git exists
- Technical users get a familiar git workflow without leaving the editor
- The transition between "just files" and "git repo" is painless

---

## Open questions

### Backend: libgit2sharp vs git CLI?

**libgit2sharp** — in-process, no external dependency, full API control.
Drawbacks: adds a native dependency (libgit2), may lag behind git features,
needs maintenance for platform-specific builds.

**git CLI** — always up to date, users can install their own version, handles
credentials/SSH natively. Drawbacks: process spawning overhead, output parsing,
requires git to be installed.

**Hybrid?** Use git CLI for operations that benefit from the user's
configuration (push, pull, credential handling) and libgit2sharp for read-heavy
operations (status, log, blame) where process overhead adds up.

### How do internal checkpoints map to git commits?

Options:
- **1:1** — every checkpoint becomes a git commit. Simple but noisy.
- **Selective** — only named/explicit checkpoints become commits. Auto-snapshots
  stay internal. This feels right — auto-snapshots are "undo history", explicit
  checkpoints are "things worth committing."
- **User chooses** — checkpoint UI has a "also commit" toggle.

### Do we expose git's staging area?

Git's index (staging area) is powerful but confusing for non-git users.

Options:
- **Hide it** — a checkpoint/commit always includes all modified files. Like
  `git add -A && git commit`. Simple, matches the checkpoint mental model.
- **Expose it** — show staged/unstaged state in the file tree. Lets power users
  craft partial commits. More complex UI.
- **Progressive disclosure** — default to "commit everything", but offer an
  advanced mode that exposes staging. Best of both worlds?

### Merge conflict resolution

If we support pull/merge, we need conflict resolution. The editor already has
a document model that could show conflict markers or a side-by-side diff.
This is a significant feature but a natural fit for a text editor.

### Should `.dmedit/` be gitignored?

Probably yes. The internal history is per-machine, per-session state (like
`.vs/` or `.idea/`). It shouldn't clutter the git repo.

But: if multiple people use dmedit on the same repo, shared project settings
(not history) could live in a committed `.dmedit/config` file, similar to
`.editorconfig`.

---

## Interesting possibilities

### Git blame in the gutter

Show blame annotations in the line number gutter — author, date, commit
message. Click to expand. This is a killer feature for code review workflows.
Could blend internal history (for uncommitted changes) with git history.

### Branch visualization

A simple branch graph in a panel. Don't need to compete with GitKraken or
SourceTree, but a basic visual of branches and their relationships helps
orientation.

### Stash integration

Auto-stash when switching branches, auto-pop on return. The internal history
system could handle this more gracefully than git stash — checkpoint the
current state, switch branches, restore on return.

### Commit message editing

Since dmedit IS a text editor, commit message composition could be a
first-class experience — inline Markdown preview, spell check, subject line
length indicator, conventional commit helpers.

### .gitignore awareness

When showing the file tree (Layer 2 projects), respect .gitignore patterns.
Show ignored files dimmed or hidden. This prevents accidental commits of
build artifacts.

---

## Things to research

- How do VS Code, JetBrains, and Sublime handle git integration UX?
- What does libgit2sharp's API surface look like for common operations?
- How does `git worktree` interact with the project model?
- Performance of `git status` on large repos — do we need to cache or
  use `fsmonitor`?
- Credential management across platforms (Windows Credential Manager,
  macOS Keychain, Linux secret service)
