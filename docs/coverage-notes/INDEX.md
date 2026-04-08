# Coverage Notes Index

One entry per file in `src/`. Each link leads to a scratch analysis.

**Start here**: [TRIAGE.md](TRIAGE.md) — cross-cutting themes and
priority list (the "what to fix first" summary).

See also [README.md](README.md) for how these notes were generated
and what the categories mean.

## Core

### Collections
- [core-collections-FenwickTree.md](core-collections-FenwickTree.md)
- [core-collections-IntFenwickTree.md](core-collections-IntFenwickTree.md)
- [core-collections-LineIndexTree.md](core-collections-LineIndexTree.md)

### Buffers
- [core-buffers-IBuffer.md](core-buffers-IBuffer.md)
- [core-buffers-IProgressBuffer.md](core-buffers-IProgressBuffer.md)
- [core-buffers-StringBuffer.md](core-buffers-StringBuffer.md)
- [core-buffers-ChunkedUtf8Buffer.md](core-buffers-ChunkedUtf8Buffer.md)
- [core-buffers-PagedFileBuffer.md](core-buffers-PagedFileBuffer.md)
- [core-buffers-StreamingFileBuffer.md](core-buffers-StreamingFileBuffer.md)

### Document
- [core-document-Piece.md](core-document-Piece.md)
- [core-document-PieceTable.md](core-document-PieceTable.md)
- [core-document-Document.md](core-document-Document.md)
- [core-document-Selection.md](core-document-Selection.md)
- [core-document-ColumnSelection.md](core-document-ColumnSelection.md)
- [core-document-CaseTransform.md](core-document-CaseTransform.md)
- [core-document-CodepointBoundary.md](core-document-CodepointBoundary.md)
- [core-document-ExpandSelectionMode.md](core-document-ExpandSelectionMode.md)

### Document/History
- [core-history-IDocumentEdit.md](core-history-IDocumentEdit.md)
- [core-history-SpanInsertEdit.md](core-history-SpanInsertEdit.md)
- [core-history-DeleteEdit.md](core-history-DeleteEdit.md)
- [core-history-CompoundEdit.md](core-history-CompoundEdit.md)
- [core-history-UniformBulkReplaceEdit.md](core-history-UniformBulkReplaceEdit.md)
- [core-history-VaryingBulkReplaceEdit.md](core-history-VaryingBulkReplaceEdit.md)
- [core-history-EditHistory.md](core-history-EditHistory.md)

### Documents (line/encoding/indent metadata)
- [core-documents-LineScanner.md](core-documents-LineScanner.md)
- [core-documents-EncodingInfo.md](core-documents-EncodingInfo.md)
- [core-documents-IndentInfo.md](core-documents-IndentInfo.md)
- [core-documents-LineEnding.md](core-documents-LineEnding.md)
- [core-documents-LineTerminatorType.md](core-documents-LineTerminatorType.md)

### IO
- [core-io-FileLoader.md](core-io-FileLoader.md)
- [core-io-FileSaver.md](core-io-FileSaver.md)

### Blocks
- [core-blocks-Block.md](core-blocks-Block.md)
- [core-blocks-BlockDocument.md](core-blocks-BlockDocument.md)
- [core-blocks-BlockPosition.md](core-blocks-BlockPosition.md)
- [core-blocks-BlockStructureChangeKind.md](core-blocks-BlockStructureChangeKind.md)
- [core-blocks-BlockStructureChangedEventArgs.md](core-blocks-BlockStructureChangedEventArgs.md)
- [core-blocks-BlockType.md](core-blocks-BlockType.md)
- [core-blocks-InlineSpan.md](core-blocks-InlineSpan.md)
- [core-blocks-InlineSpanType.md](core-blocks-InlineSpanType.md)

### Styles
- [core-styles-BlockStyle.md](core-styles-BlockStyle.md)
- [core-styles-InlineStyle.md](core-styles-InlineStyle.md)
- [core-styles-StyleSheet.md](core-styles-StyleSheet.md)

### Printing
- [core-printing-ISystemPrintService.md](core-printing-ISystemPrintService.md)
- [core-printing-PrintJobTicket.md](core-printing-PrintJobTicket.md)
- [core-printing-PrintSettings.md](core-printing-PrintSettings.md)
- [core-printing-PrinterInfo.md](core-printing-PrinterInfo.md)

### Clipboard
- [core-clipboard-INativeClipboardService.md](core-clipboard-INativeClipboardService.md)

## Rendering
- [rendering-TextLayoutEngine.md](rendering-TextLayoutEngine.md)
- [rendering-MonoLayoutContext.md](rendering-MonoLayoutContext.md)
- [rendering-MonoLineLayout.md](rendering-MonoLineLayout.md)
- [rendering-BlockLayoutEngine.md](rendering-BlockLayoutEngine.md)
- [rendering-BlockLayoutLine.md](rendering-BlockLayoutLine.md)
- [rendering-BlockLayoutResult.md](rendering-BlockLayoutResult.md)
- [rendering-LayoutLine.md](rendering-LayoutLine.md)
- [rendering-LayoutResult.md](rendering-LayoutResult.md)
- [rendering-LineTooLongException.md](rendering-LineTooLongException.md)

## App/Commands
- [app-commands-Command.md](app-commands-Command.md)
- [app-commands-Commands.md](app-commands-Commands.md)
- [app-commands-ChordGesture.md](app-commands-ChordGesture.md)
- [app-commands-KeyBindingService.md](app-commands-KeyBindingService.md)
- [app-commands-KeyGestureComparer.md](app-commands-KeyGestureComparer.md)
- [app-commands-ProfileData.md](app-commands-ProfileData.md)
- [app-commands-ProfileLoader.md](app-commands-ProfileLoader.md)

## App/Controls
- [app-controls-EditorControl.md](app-controls-EditorControl.md)
- [app-controls-CaretLayer.md](app-controls-CaretLayer.md)
- [app-controls-DMInputBox.md](app-controls-DMInputBox.md)
- [app-controls-DMTextBox.md](app-controls-DMTextBox.md)
- [app-controls-DMEditableCombo.md](app-controls-DMEditableCombo.md)
- [app-controls-DualZoneScrollBar.md](app-controls-DualZoneScrollBar.md)
- [app-controls-TabBarControl.md](app-controls-TabBarControl.md)
- [app-controls-ToolbarControl.md](app-controls-ToolbarControl.md)
- [app-controls-FindBarControl.md](app-controls-FindBarControl.md)
- [app-controls-IScrollSource.md](app-controls-IScrollSource.md)
- [app-controls-UiHelpers.md](app-controls-UiHelpers.md)

## App/Services
- [app-services-AppSettings.md](app-services-AppSettings.md)
- [app-services-ClipboardRing.md](app-services-ClipboardRing.md)
- [app-services-CrashReport.md](app-services-CrashReport.md)
- [app-services-EditSerializer.md](app-services-EditSerializer.md)
- [app-services-EditorTheme.md](app-services-EditorTheme.md)
- [app-services-FeedbackClient.md](app-services-FeedbackClient.md)
- [app-services-FeedbackPayload.md](app-services-FeedbackPayload.md)
- [app-services-FileConflict.md](app-services-FileConflict.md)
- [app-services-FileWatcherService.md](app-services-FileWatcherService.md)
- [app-services-GitHubIssueHelper.md](app-services-GitHubIssueHelper.md)
- [app-services-LinuxClipboardService.md](app-services-LinuxClipboardService.md)
- [app-services-LinuxFileDialog.md](app-services-LinuxFileDialog.md)
- [app-services-NativeClipboardDiscovery.md](app-services-NativeClipboardDiscovery.md)
- [app-services-PdfGenerator.md](app-services-PdfGenerator.md)
- [app-services-PerfLog.md](app-services-PerfLog.md)
- [app-services-RecentFilesStore.md](app-services-RecentFilesStore.md)
- [app-services-SessionStore.md](app-services-SessionStore.md)
- [app-services-SingleInstanceService.md](app-services-SingleInstanceService.md)
- [app-services-UpdateService.md](app-services-UpdateService.md)
- [app-services-WindowsPrintService.md](app-services-WindowsPrintService.md)

## App/Settings
- [app-settings-CommandsSettingsSection.md](app-settings-CommandsSettingsSection.md)
- [app-settings-SettingDescriptor.md](app-settings-SettingDescriptor.md)
- [app-settings-SettingRowFactory.md](app-settings-SettingRowFactory.md)
- [app-settings-SettingsControl.md](app-settings-SettingsControl.md)
- [app-settings-SettingsRegistry.md](app-settings-SettingsRegistry.md)

## App/Misc
- [app-AboutDialog.md](app-AboutDialog.md)
- [app-App.md](app-App.md)
- [app-ClipboardRingWindow.md](app-ClipboardRingWindow.md)
- [app-CommandPaletteWindow.md](app-CommandPaletteWindow.md)
- [app-ErrorDialog.md](app-ErrorDialog.md)
- [app-FileConflictChoice.md](app-FileConflictChoice.md)
- [app-GoToLineWindow.md](app-GoToLineWindow.md)
- [app-IconGlyphs.md](app-IconGlyphs.md)
- [app-MainWindow.md](app-MainWindow.md)
- [app-PrintDialog.md](app-PrintDialog.md)
- [app-Program.md](app-Program.md)
- [app-ProgressDialog.md](app-ProgressDialog.md)
- [app-SaveChangesDialog.md](app-SaveChangesDialog.md)
- [app-SubmitFeedbackDialog.md](app-SubmitFeedbackDialog.md)
- [app-TabState.md](app-TabState.md)

## Windows
- [windows-NativeClipboardService.md](windows-NativeClipboardService.md)
- [windows-WpfPrintService.md](windows-WpfPrintService.md)
