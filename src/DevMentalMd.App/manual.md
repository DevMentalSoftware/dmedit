# DMEdit Manual

DMEdit is a text editor designed for speed, even with very large files.
This document describes every operation currently available.


## Typing and Editing

| Action              | Key              |
|---------------------|------------------|
| Insert text         | any printable key|
| New line            | Enter            |
| Insert spaces (tab) | Tab              |
| Delete backward     | Backspace        |
| Delete forward      | Delete           |
| Delete entire line  | Ctrl+Y           |
| Undo                | Ctrl+Z           |
| Redo                | Ctrl+Shift+Z     |


## Caret Movement

| Action                         | Key                |
|--------------------------------|--------------------|
| Move left / right              | Left / Right       |
| Move up / down                 | Up / Down          |
| Word left / right              | Ctrl+Left / Right  |
| Line start / end               | Home / End         |
| Page up / down                 | PageUp / PageDown  |


## Selection

| Action                         | Key                     |
|--------------------------------|-------------------------|
| Extend selection               | Shift + any movement key|
| Select all                     | Ctrl+A                  |
| Select word at caret           | Ctrl+W                  |
| Click to place caret           | Left click              |
| Shift-click to extend          | Shift + left click      |
| Drag to select                 | Left click + drag       |


## Case Transforms

| Action       | Key              |
|--------------|------------------|
| UPPER CASE   | Ctrl+Shift+U     |
| lower case   | Ctrl+Shift+L     |
| Proper Case  | Ctrl+Shift+P     |


## Line Operations

| Action         | Key         |
|----------------|-------------|
| Move line up   | Alt+Up      |
| Move line down | Alt+Down    |
| Delete line    | Ctrl+Y      |


## Scrolling

| Action                  | Method                     |
|-------------------------|----------------------------|
| Scroll by 3 rows        | Mouse wheel                |
| Page scroll              | PageUp / PageDown          |
| Scroll bar drag          | Drag the scroll thumb      |
| Track click page scroll  | Click above/below thumb    |
| Middle-drag scroll       | Middle mouse button + drag |


## File Menu

| Action        | Menu                   |
|---------------|------------------------|
| New document  | File > New             |
| Open file     | File > Open            |
| Save          | File > Save            |
| Save as       | File > Save As         |
| Close         | File > Close           |
| Recent files  | File > (recent list)   |


## View Menu

| Setting        | Menu               | Notes                             |
|----------------|--------------------|------------------------------------|
| Line numbers   | View > Line Numbers| Toggle gutter line numbers         |
| Status bar     | View > Status Bar  | Ln / Ch / line-count display       |
| Statistics     | View > Statistics  | Layout, render, I/O timing (dev)   |
| Wrap lines     | View > Wrap Lines  | Soft-wrap at window or column guide |


## Status Bar

When enabled, the status bar shows:

- **Ln** -- current line number
- **Ch** -- current column number
- **Line count** -- total lines in the document
- **Encoding** -- UTF-8
- **Line ending** -- LF
- **Indent style** -- Spaces


## Column Guide

When `WrapLinesAt` is set to a positive value (default 100), a 
vertical guide line is drawn at that column.  Text wraps at the guide or
the window edge, whichever is narrower.  Set `WrapLinesAt` to 0 to disable.


## Large File Support

Multi-gigabyte files open and scroll smoothly.  Memory usage stays low
regardless of file size.
