* Add Reset button for any changed setting on the settings page (we could do it as a context menu if we don't want to change ui).
* Add zoom button to status bar. This will display the current zoom level, and provide quick reset to 100%, and dropdown of common choices. There should also be zoom in/out commands, and support for Ctrl+Mousewheel within the editor as well as a command to reset to 100% zoom.
* Remove Zoom menu items. We'll put this on the task bar, and bind keyboard shortcuts.
* Add feature to track usage count of commands with separate counters for menu vs keyboard vs command palette use.
* Add feature to hide menus that have high keyboard use (with the assumption the user has learned the shortcut.)
* Add file history feature based on a hidden git implementation. This would only be used on files not already within a git repo. We're making our own simplified interface on top of git to allow Blame, Checkpoints/Log, Commit/Amend, and other features.
* Add a Tail command boolean setting. When enabled then if ReloadFile occurs \*and\* we are currently scrolled to the end of the document \*and\* the file doesn't have any unsaved edits, then automatically keep the document scrolled to the bottom to see any new content. However, if we are currently scrolled somewhere else in the document then the reload should not move the current scroll position (unless it's no longer available such as when the external change was to delete that part of the file.)
* Add tab context menu to open the file location in Explorer (or equiv unix).
* Add tab context menu to copy the path to the clipboard.
* The popup when closing a file with changes has the wrong background color. The system Open and Save dialogs have a different look than the reset of the app, but maybe any modal popups should try to match that look? Or we could stick with our custom look for our own non-system dialogs. I think the background should match our menu/statusbar, and the buttons should act like our other buttons.
* Same for the Close All popup.
* Add Toolbar buttons for Open, Save, Save All, Wrap, Cut, Copy, Paste, Find, Show Whitespace, Tail, Command Palette.
* Add an option for whether to subtly highlight (like selection but gray translucent) all text in the document that matches the current selection. This is something that some editors do and people might want it, though I personally hate it.
* If one or more crash reports exist, then a Help menu item should be added to send the crash reports to support@devmental.com.
* We need context menus in the editor, but need to decide what subset of commands should be there.





Another idea. We use char and String several places. Does dotnet still internally use 16bit characters for these structures? I remember some talk about switching to UTF8 by default, but don't know if it happened.







