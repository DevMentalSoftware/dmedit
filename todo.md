* Add Reset button for any changed setting on the settings page (we could do it as a context menu if we don't want to change ui).
* Add zoom button to status bar. This will display the current zoom level, and provide quick reset to 100%, and dropdown of common choices. There should also be zoom in/out commands, and support for Ctrl+Mousewheel within the editor as well as a command to reset to 100% zoom.
* Remove Zoom menu items. We'll put this on the task bar, and bind keyboard shortcuts.
* Add feature to track usage count of commands with separate counters for menu vs keyboard vs command palette use.
* Add feature to hide menus that have high keyboard use (with the assumption the user has learned the shortcut.)
* Add file history feature based on a hidden git implementation. This would only be used on files not already within a git repo. We're making our own simplified interface on top of git to allow Blame, Checkpoints/Log, Commit/Amend, and other features.
* If we close a file while it's still loading then loading should cancel. The session state should know enough about the file to know whether it has unsaved edits, so this shouldn't impact the logic for asking the user what to do as usual.
* Detect changes in open files, and handle the same as when we detect changes at open of session. May need to revise the latter to account for possible actions. One of the possibilities is always to discard unsaved edits and load the new base file. Another would be to merge the unsaved edits into the new base (like a diff utility would do). A third option would be to do nothing so that if the user changes the base file to match the original then we're back to a good state. These latter two options might really be the same, because as long as we don't actually modify the base again, it would be ok to show the current edits as if they're merged into the new base.
* Add a setting for picking the Editor Font. Show the system fonts as a list, but have a toggle button to filter to fixed width fonts by default. For the initial default, prefer "Cascadia Code", Consolas, or "Courier", and something appropriate for Linux.
* Add support for Selecting by column using either Alt-Shift-ArrowUpDown or Alt-MouseDrag.  Support duplicating the caret across each row of the selection and making simultaneous edits. (NP++, VS, and others all support this)
* When opening a file and the only current tab is the Unnamed tab then replace that tab with the new opened one.
* Command Palette should not put the category after each command. Instead organize by category like we did in the keyboard settings. In fact this is almost a duplicate of the keyboard settings command list and search box, but with enter key or double click of a command actually running the command.
* Add a Tail command boolean setting. When enabled then if ReloadFile occurs \*and\* we are currently scrolled to the end of the document \*and\* the file doesn't have any unsaved edits, then automatically keep the document scrolled to the bottom to see any new content. However, if we are currently scrolled somewhere else in the document then the reload should not move the current scroll position (unless it's no longer available such as when the external change was to delete that part of the file.)
* Add tab context menu to open the file location in Explorer (or equiv unix).
* Add tab context menu to copy the path to the clipboard.
* Bug. Hit Enter at end of last line and the new line is not scrolled into view, and can't see the caret. This is a return of a bug we fixed weeks ago, so not sure what changed.
* The popup when closing a file with changes has the wrong background color. The system Open and Save dialogs have a different look than the reset of the app, but maybe any modal popups should try to match that look? Or we could stick with our custom look for our own non-system dialogs. I think the background should match our menu/statusbar, and the buttons should act like our other buttons.
* Same for the Close All popup.
* Add Toolbar buttons for Open, Save, Save All, Wrap, Cut, Copy, Paste, Find, Show Whitespace, Tail, Command Palette.
* 

