Notes :



Thickness(left, top, right, bottom)



* Add Reset button for any changed setting on the settings page (we could do it as a context menu if we don't want to change ui).
* Add zoom button to status bar. This will display the current zoom level, and provide quick reset to 100%, and dropdown of common choices. There should also be zoom in/out commands, and support for Ctrl+Mousewheel within the editor as well as a command to reset to 100% zoom.
* Add feature to track usage count of commands with separate counters for menu vs keyboard vs command palette use.
* Add feature to hide menus that have high keyboard use (with the assumption the user has learned the shortcut.)
* Add support for other charsets and BOM optional
* Add support for displaying non-printable characters, and toggle options for common characters like (ShowLineEndings, ShowWhitespace, ShowSpecial)
* Test modifying a base file out from under unsaved edits. We may want to either save base files for the session below a threshold, or even avoid fixing corrupted edits like this at all. Or even allow merging the edits into the file with markers for the user to fix. If the latter then we'd have to be careful to keep track of offsets as we change the document while applying edits. Basically we'd through away edits to make a new baseline that has the edits inserted at the old positions in the new document with begin/end markers to show the issue.
* Add file history feature based on a hidden git implementation. This would only be used on files not already within a git repo. We're making our own simplified interface on top of git to allow Blame, Checkpoints/Log, Commit/Amend, and other features.
* If we close a file while it's still loading then loading should cancel. The session state should know enough about the file to know whether it has unsaved edits, so this shouldn't impact the logic for asking the user what to do as usual.
* Detect changes in open files, and handle the same as when we detect changes at open of session. May need to revise the latter to account for possible actions. One of the possibilities is always to discard unsaved edits and load the new base file. Another would be to merge the unsaved edits into the new base (like a diff utility would do). A third option would be to do nothing so that if the user changes the base file to match the original then we're back to a good state. These latter two options might really be the same, because as long as we don't actually modify the base again, it would be ok to show the current edits as if they're merged into the new base.
* Add a setting for picking the Editor Font. Show the system fonts as a list, but have a toggle button to filter to fixed width fonts by default. For the initial default, prefer "Cascadia Code", Consolas, or "Courier", and something appropriate for Linux.
* Implement the PasteMore Ctrl-Shift-V to paste from a Clipboard Ring. 
* Add support for Selecting by column using either Alt-Shift-ArrowUpDown or Alt-MouseDrag.  Support duplicating the caret across each row of the selection and making simultaneous edits. (NP++, VS, and others all support this)
* Remove the Select All Occurrences menu in favor of just having a command for this, and/or a button on the FindBar.
* Remove Zoom menu items. We'll put this on the task bar, and bind keyboard shortcuts.
* When opening a file and the only current tab is the Unnamed tab then replace that tab with the new opened one. 
* Ctrl+F3 should not pull up the FindBar or change the Find Bar text. It should immediately jump to the next occurrence of the current word.
* Ctrl+I should likewise not pull up the FindBar. Instead it should intercept all keys as if there is a hidden text box, and as each letter is typed should immediately jump to the matching word or expand the current match if the next letter matches. 
* Command Palette should not put the category after each command. Instead organize by category like we did in the keyboard settings. In fact this is almost a duplicate of the keyboard settings command list and search box, but with enter key or double click of a command actually running the command. 

