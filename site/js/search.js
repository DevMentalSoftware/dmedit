// search.js — Client-side search for DMEdit documentation.
// Works on file:// because search-data.js is loaded via <script> tag.

(function () {
    "use strict";

    var input = document.getElementById("search-input");
    var resultsEl = document.getElementById("search-results");
    if (!input || !resultsEl) return;

    var data = window.SEARCH_DATA || [];

    // Determine URL prefix based on page depth.
    var path = window.location.pathname.replace(/\\/g, "/");
    var prefix = path.indexOf("/manual/") !== -1 ? "../" : "";

    input.addEventListener("input", function () {
        var query = input.value.trim().toLowerCase();
        if (!query) {
            resultsEl.classList.remove("visible");
            resultsEl.innerHTML = "";
            return;
        }

        var terms = query.split(/\s+/);
        var matches = [];

        for (var i = 0; i < data.length; i++) {
            var entry = data[i];
            var haystack = (entry.title + " " + entry.keywords + " " + entry.headings).toLowerCase();
            var allMatch = true;
            var matchedHeading = "";

            for (var t = 0; t < terms.length; t++) {
                if (haystack.indexOf(terms[t]) === -1) {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch) {
                // Find a matching heading to show as context.
                var headings = (entry.headings || "").split("|");
                for (var h = 0; h < headings.length; h++) {
                    if (headings[h].toLowerCase().indexOf(terms[0]) !== -1) {
                        matchedHeading = headings[h];
                        break;
                    }
                }
                matches.push({ entry: entry, heading: matchedHeading });
            }
        }

        if (matches.length === 0) {
            resultsEl.innerHTML = '<div class="search-no-results">No results found</div>';
            resultsEl.classList.add("visible");
            return;
        }

        var html = "";
        for (var m = 0; m < matches.length; m++) {
            var match = matches[m];
            html += '<a class="search-result-item" href="' + prefix + match.entry.url + '">';
            html += '<div class="search-result-title">' + match.entry.title + "</div>";
            if (match.heading) {
                html += '<div class="search-result-match">' + match.heading + "</div>";
            }
            html += "</a>";
        }
        resultsEl.innerHTML = html;
        resultsEl.classList.add("visible");
    });

    // Close results when clicking outside.
    document.addEventListener("click", function (e) {
        if (!e.target.closest(".search-wrapper")) {
            resultsEl.classList.remove("visible");
        }
    });

    // Re-open results when focusing the search input if it has a value.
    input.addEventListener("focus", function () {
        if (input.value.trim() && resultsEl.innerHTML) {
            resultsEl.classList.add("visible");
        }
    });
})();
