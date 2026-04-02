# Contributing to DMEdit

Thanks for your interest in DMEdit! This document covers what you need to get
started.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build and test

```bash
dotnet build
dotnet test
```

All tests must pass before and after any change.

## Coding style

Follow the conventions in [`docs/csharp-style.md`](docs/csharp-style.md) — K&R
braces, modern C# features, and the naming rules described there.

Project-specific abbreviations and reserved names are listed in
[`docs/project-conventions.md`](docs/project-conventions.md).

## Reporting bugs

Open an issue using the **Bug report** template. Include:

- Steps to reproduce
- Expected vs actual behavior
- OS and DMEdit version (Help → About)

## Suggesting features

Open an issue using the **Feature request** template. Describe the problem
you're trying to solve before proposing a solution.

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Make your changes and ensure all tests pass.
3. Fill out the PR template — a short summary and how you tested it.
4. Keep PRs focused: one logical change per PR.
