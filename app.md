This document describes a plan for a WYSIWYG Markdown editor. 

The initial plan is to base it around AvaloniaUI as a more cross platform capable dotnet solution.

We'll try to avoid the need to ever show the underlying Markdown syntax, preferring instead to allow
directly editing the text already formatted as it will be displayed.

Rather than build on higher level Avalonia components, we will attempt to build only from relatively
lower level primitives. I'm more familiar with WPF than Avalonia so keep that in mind for discussions about
features. 

We'll want to support styles to specify how the documents will be formatted. For example these can specify
the Font, Size, Line Spacing, etc. We will also want to allow specifying color options and other similar
layout options so that it's easy to view a given document in a chosen style. 

The in-memory representation of a document should **not** be simple Markdown text. Rather it should be 
some sort of binary piece-table implementation with support for undo/redo, history, etc.

Automatic opt-in to common extended Markdown syntax by displaying the detected flavor in a Status bar with
easy ability to switch to a different explicit translation if desired.

I don't think we should have an intermediate HTML step in the rendering. 

We want to support very large documents efficiently.

We want to allow persisted sessions where even unsaved changes are retained.

Document history should let me "scroll" forward and back through all the saved versions of a file, optionally
with the ability to highlight changes. Maybe with a colored side bar, and/or underbar to highlight the changed
words within a line. This is separate from the ability to show two versions simultaneously with changes
highlighted, but not sure if it's a separate feature or not. 

Don't modify existing md files to remove unnecessary line breaks, but don't put new line breaks within a paragraph. Instead we should support configurable wrap width. Do provide a simple option to standardize all formatting, and enable this is a button if non-standard or non-recommended formatting is detected.

Similarly don't modify an md file automtically to standardize use of asterisks vs underscores to mark bold and italic. Prefer always using asterisks. Same for '+', '*', and '-' for unordered lists. We will prefer '-'.

***

#Technology

* Markdown -- Treat https://www.markdownguide.org/ as the source for syntax for basic and extended features.

* Avalonia UI -- We'll primarily develop on Windows, but would like to get good support for Linux and Mac as well.

* Avoid other libraries -- Reinvent those wheels.

* dotnet latest -- We have the latest Visual Studio Professional so might as well use it.

* C# -- Create a separate spec md file to ensure we always choose consistent style, and avoid older features 
that have been superseded by superior ones. (e.g. lambdas instead of delegates)  


