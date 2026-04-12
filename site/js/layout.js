// layout.js — Shared header + sidebar for all DMEdit documentation pages.
// Each page sets <body data-page="page-id"> to indicate the active nav item.

(function () {
    "use strict";

    var NAV = [
        { title: "Home", page: "index", url: "index.html" },
        { title: "What's New", page: "whats-new", url: "whats-new.html" },
        {
            section: "Manual", children: [
                { title: "Getting Started", page: "getting-started", url: "manual/getting-started.html" },
                { title: "File Operations", page: "file-operations", url: "manual/file-operations.html" },
                { title: "Editing", page: "editing", url: "manual/editing.html" },
                { title: "Navigation & Selection", page: "navigation-selection", url: "manual/navigation-selection.html" },
                { title: "Search & Replace", page: "search-replace", url: "manual/search-replace.html" },
                { title: "Display & View", page: "display-view", url: "manual/display-view.html" },
                { title: "Tabs & Windows", page: "tabs-windows", url: "manual/tabs-windows.html" },
                { title: "Status Bar", page: "status-bar", url: "manual/status-bar.html" },
                { title: "Settings", page: "settings", url: "manual/settings.html" },
                { title: "Keyboard Shortcuts", page: "keyboard-shortcuts", url: "manual/keyboard-shortcuts.html" },
                { title: "Large Files", page: "large-files", url: "manual/large-files.html" },
            ]
        }
    ];

    // Determine if we're in a subdirectory (e.g. manual/) and need a prefix.
    var path = window.location.pathname.replace(/\\/g, "/");
    var prefix = "";
    if (path.indexOf("/manual/") !== -1) {
        prefix = "../";
    }

    var activePage = document.body.getAttribute("data-page") || "";

    // --- Header ---
    var headerEl = document.getElementById("site-header");
    if (headerEl) {
        var isManualPage = NAV[2].children.some(function (c) { return c.page === activePage; });
        var headerHtml = '<button id="menu-toggle" aria-label="Open navigation">&#9776;</button>';
        headerHtml += '<div class="header-title"><a href="' + prefix + 'index.html">DMEdit</a></div>';
        headerHtml += '<div class="header-nav">';
        headerHtml += '<a href="' + prefix + 'whats-new.html"' + (activePage === "whats-new" ? ' class="active"' : '') + ">What's New</a>";
        headerHtml += '<a href="' + prefix + 'manual/getting-started.html"' + (isManualPage ? ' class="active"' : '') + ">Manual</a>";
        headerHtml += "</div>";
        headerHtml += '<div class="header-spacer"></div>';
        headerHtml += '<div class="search-wrapper">';
        headerHtml += '<span class="search-icon">&#128269;</span>';
        headerHtml += '<input type="text" id="search-input" placeholder="Search docs..." autocomplete="off" />';
        headerHtml += '<div id="search-results"></div>';
        headerHtml += "</div>";
        headerEl.innerHTML = headerHtml;
    }

    // --- Sidebar ---
    var sidebarEl = document.getElementById("sidebar");
    if (sidebarEl) {
        var sideHtml = "";
        for (var i = 0; i < NAV.length; i++) {
            var item = NAV[i];
            if (item.section) {
                sideHtml += '<div class="nav-section-title">' + item.section + "</div>";
                for (var j = 0; j < item.children.length; j++) {
                    var child = item.children[j];
                    var isActive = child.page === activePage;
                    sideHtml += '<a href="' + prefix + child.url + '"' + (isActive ? ' class="active"' : '') + ">" + child.title + "</a>";
                }
            } else {
                var isActive2 = item.page === activePage;
                sideHtml += '<a href="' + prefix + item.url + '"' + (isActive2 ? ' class="active"' : '') + ">" + item.title + "</a>";
            }
        }
        sidebarEl.innerHTML = sideHtml;
    }

    // --- Mobile menu toggle ---
    var overlay = document.createElement("div");
    overlay.className = "sidebar-overlay";
    document.body.appendChild(overlay);

    var toggleBtn = document.getElementById("menu-toggle");
    if (toggleBtn && sidebarEl) {
        toggleBtn.addEventListener("click", function () {
            sidebarEl.classList.toggle("open");
            overlay.classList.toggle("visible");
        });
        overlay.addEventListener("click", function () {
            sidebarEl.classList.remove("open");
            overlay.classList.remove("visible");
        });
    }
})();
