# C# Style Guide

This document defines the coding conventions for the DMEdit project.
All code should follow these rules consistently. When in doubt, match what's already in the codebase.

---

## One statement per line — always

**Every statement gets its own line, including the body of a single-statement
control-flow block.** This rule has no exceptions for "small" or "obvious"
cases — guard returns, `if (cond) continue;`, `if (cond) break;`, `if
(cond) throw ...;`, and short `if`/`else` chains all expand to multi-line
form with braces.

The two reasons (both important):

1. **Breakpoints.** A debugger can only stop at line granularity.
   `if (x is null) return;` on one line means I can't break on the
   `return` without also breaking on every evaluation of the condition.
2. **Unambiguous references.** "Line 1216" should identify exactly one
   statement — so we can talk about behavior or correctness without
   pointing at "the second statement on line 1216."

Wasting a line on a closing brace is fine — readability and debuggability
beat line count.

```csharp
// ❌ Don't — two statements share a line.
if (CaretPosition is null) return;
if (sourceIdx < 0) return;
while (i < n) i++;

// ✅ Do — every statement on its own line, every body braced.
if (CaretPosition is null) {
    return;
}
if (sourceIdx < 0) {
    return;
}
while (i < n) {
    i++;
}
```

This applies to **new code we write together.** Existing code that doesn't
follow this rule will be migrated opportunistically (e.g. when touching a
function for unrelated reasons) — don't do bulk style-only edits.

The only carve-out is the one already noted in the *Brace Style* section:
expression-bodied members (`=>`) for single-expression properties and
methods. Those are not control flow and stay one-liners.

## Avoid chained dotted access in most cases

A common pattern is to check a nullable reference for null, then to use that variable many times
in a method body, but I prefer to assign to a non-nullable temporary if used more than once.

Don't use this:
```
   void someMethod(A? p1) {
       var a = p1;
       if (p1 is null) {
          return;
       }
       a.Value.x();
       a.Value.y();
```
Instead use this:
```
   void someMethod(A? p1) {
       if (p1 is null) {
          return;
       }
       var a = p1.Value;
       a.x();
       a.y();
```

There are other common places where chaining together multiple dots or question marks in a single statement are common, but whenever that would be reused multiple times I'd prefer assigning a local variable.

## Brace Style

Opening braces go on the **same line** (K&R / Java style). Closing braces are on their own line.

```csharp
public class Foo {
    public void Bar() {
        if (condition) {
            DoSomething();
        } else {
            DoOther();
        }
    }
}
```

Always use braces for all control-flow constructs, even single-statement bodies:

```csharp
// Good
if (x > 0) {
    return x;
}

// Bad — no braces
if (x > 0)
    return x;
```

Exception: expression-bodied members (`=>`) are fine for single-expression properties and methods:

```csharp
public int Count => _pieces.Count;
public string GetText() => string.Concat(_pieces.Select(p => p.Buf));
```

---

## Naming

### General rule: name length proportional to scope

A local variable in a short function can be one or two letters. A field in a large class needs to be descriptive enough to be clear without surrounding context. Prefer the shortest unambiguous name.

```csharp
// Local in a short loop — fine
for (var i = 0; i < len; i++) { ... }

// Field in a large class — needs more context
private int _minCharCount;
```

### Casing

| Element | Convention | Example |
|---|---|---|
| Types (class, struct, enum, interface, record) | PascalCase | `PieceTable`, `BufferKind` |
| Public members (methods, properties, events) | PascalCase | `Insert()`, `LineCount` |
| Private fields | `_camelCase` | `_pieces`, `_addBuf` |
| Local variables and parameters | camelCase | `offset`, `lineIdx` |
| Constants | PascalCase | `MaxLineLength` |
| Interfaces | `I` prefix + PascalCase | `IDocumentEdit` |

No Hungarian notation. No type-encoded prefixes (`strName`, `bFlag`, etc.).

### Abbreviations

Use standard abbreviations for very common words. See `project-conventions.md` for the project abbreviation list. When uncertain, prefer a short spelled-out name over an obscure abbreviation.

### Avoid name collisions

Do not name types or members the same as types in common BCL namespaces (e.g., avoid `Path`, `Stream`, `Task`, `List`, `File`). See `project-conventions.md` for the reserved-names list.

---

## Language Features

Prefer modern C# features over older equivalents. This codebase is an opportunity to learn and use current C#.

### Records and record structs

Use `record` or `record struct` for immutable value types:

```csharp
record struct Piece(BufferKind Which, int Start, int Len);
```

### Pattern matching

Prefer pattern matching over `is`/`as` casts:

```csharp
// Good
if (edit is InsertEdit ins) { ... }

// Good — switch expression
var result = edit switch {
    InsertEdit ins => ins.Apply(table),
    DeleteEdit del => del.Apply(table),
    _ => throw new ArgumentOutOfRangeException()
};
```

### Switch expressions

Prefer `switch` expressions over `switch` statements where the result is a value:

```csharp
var label = kind switch {
    BufferKind.Original => "orig",
    BufferKind.Add => "add",
    _ => "unknown"
};
```

### Lambdas

Use lambdas instead of named delegates:

```csharp
// Good
var lines = pieces.Where(p => p.Len > 0).ToList();

// Bad
var lines = pieces.Where(delegate(Piece p) { return p.Len > 0; }).ToList();
```

### var

Use `var` when the type is obvious from the right-hand side:

```csharp
var table = new PieceTable(content);   // obvious
var count = GetCount();                // fine if GetCount's return type is clear from context
int n = ComputeSomethingComplicated(); // explicit type helps readability
```

### Nullable reference types

Nullable reference types are enabled project-wide. Annotate all public APIs:

```csharp
public string? TryGetLine(int idx)
public void Insert(int offset, string text)  // non-nullable = never null
```

### Init-only and primary constructors

Use init-only setters and primary constructors where they reduce boilerplate:

```csharp
// Primary constructor
class EditHistory(int maxDepth) {
    private readonly int _maxDepth = maxDepth;
}
```

### Collections

Expose collections as `IReadOnlyList<T>` or `IReadOnlyDictionary<K,V>` from public APIs. Use concrete types internally.

---

## Async

- Always suffix async methods with `Async`: `LoadFileAsync()`
- Always accept a `CancellationToken` parameter (last parameter, default `default`)
- Never use `async void` except in event handlers (and mark those clearly)

---

## Other Rules

- One primary type per file; filename matches the type name
- No `#region` blocks
- Keep lines to a reasonable length (100 characters is a soft limit)
- Prefer early returns over deeply nested `if` blocks
- `IReadOnlyList` / `IReadOnlyDictionary` for public collection properties
