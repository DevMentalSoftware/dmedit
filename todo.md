Notes :



Thickness(left, top, right, bottom)



* Add Reset button for any changed setting.
* Add zoom button to status bar.
* Add feature to hide Settings that are duplicated on the Menu/Tool bar.
* Add feature to track usage count of commands with separate counters for menu vs keyboard vs command palette use.
* Add feature to hide menus that have high keyboard use (with the assumption the user has learned the shortcut.)
* Add support for CRLF and CR line endings
* Add support for other charsets and BOM optional
* Edit.SelectWord isn't working correctly. It should first look at the current selection, and if it contains any whitespace then SelectWord should do nothing. Otherwise it should expand selection backward from the beginning of the current selection and include everything up to the first nonwhitespace character. It should also expand forward from the end of the current selection to include everything up to the first non-whitespace character in that direction. In any case, it should not expand outside the current line (and if selection spans lines that should be treated as selection containing whitespace.) Depending on context there may be additional characters treated as word separators besides whitespace (e.g. '.' in many code files, or perhaps any non-alphanumeric)
* Edit.ExpandSelection works similar to SelectWord except that each time you press it it expands outword across another level of separator. We may even have a setting for whether capitalization changes indicate a separator.
* double click between two letters does SelectWord at that location.
* Use thinner and more subtle divider between menu item sections and make it fill the menu width instead of having padding/margin.

