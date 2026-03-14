Notes :



Thickness(left, top, right, bottom)



* Add Reset button for any changed setting.
* Add zoom button to status bar.
* Add feature to track usage count of commands with separate counters for menu vs keyboard vs command palette use.
* Add feature to hide menus that have high keyboard use (with the assumption the user has learned the shortcut.)
* Add support for CRLF and CR line endings
* Add support for other charsets and BOM optional
* Use Paged more for all file sizes. Streaming is just for zipped text support.
* Add support for displaying non-printable characters, and toggle options for common characters like (ShowLineEndings, ShowWhitespace, ShowSpecial)
* Test modifying a base file out from under unsaved edits. We may want to either save base files for the session below a threshold, or even avoid fixing corrupted edits like this at all. Or even allow merging the edits into the file with markers for the user to fix. If the latter then we'd have to be careful to keep track of offsets as we change the document while applying edits. Basically we'd through away edits to make a new baseline that has the edits inserted at the old positions in the new document with begin/end markers to show the issue. 
* Add file history feature based on a hidden git implementation. This would only be used on files not already within a git repo. We're making our own simplified interface on top of git to allow Blame, Checkpoints/Log, Commit/Amend, and other features. 
* If we close a file while it's still loading then loading should cancel. The session state should know enough about the file to know whether it has unsaved edits, so this shouldn't impact the logic for asking the user what to do as usual.
* Detect changes in open files, and handle the same as when we detect changes at open of session. May need to revise the latter to account for possible actions. One of the possibilities is always to discard unsaved edits and load the new base file. Another would be to merge the unsaved edits into the new base (like a diff utility would do). A third option would be to do nothing so that if the user changes the base file to match the original then we're back to a good state. These latter two options might really be the same, because as long as we don't actually modify the base again, it would be ok to show the current edits as if they're merged into the new base.

