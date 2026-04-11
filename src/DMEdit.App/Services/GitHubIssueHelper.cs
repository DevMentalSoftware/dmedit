using System.Diagnostics;
using System.Text;
using Avalonia.Controls;

namespace DMEdit.App.Services;

/// <summary>
/// Builds pre-filled GitHub issue URLs and opens them in the default browser.
/// </summary>
public static class GitHubIssueHelper {
    private static string RepoUrl => AppConstants.GitHubRepositoryUrl;
    private const int MaxUrlLength = 7500;

    /// <summary>
    /// Opens the browser with a pre-filled "Report a Bug" issue.
    /// </summary>
    public static void OpenBugReport(Window? owner = null) {
        var info = SystemInfoCollector.Collect(owner);
        var body = $"""
            **Describe the bug**
            <!-- A clear description of what went wrong. -->

            **Steps to reproduce**
            1.
            2.
            3.

            **Expected behavior**
            <!-- What you expected to happen. -->

            ---
            {info.ToMarkdownFooter()}
            """;
        OpenIssueUrl("Bug: ", body, "bug");
    }

    /// <summary>
    /// Opens the browser with a pre-filled "Suggest a Feature" issue.
    /// </summary>
    public static void OpenFeatureRequest(Window? owner = null) {
        var info = SystemInfoCollector.Collect(owner);
        var body = $"""
            **Describe the feature**
            <!-- A clear description of what you'd like. -->

            **Use case**
            <!-- Why is this feature important to your workflow? -->

            ---
            {info.ToMarkdownFooter()}
            """;
        OpenIssueUrl("Feature: ", body, "enhancement");
    }

    /// <summary>
    /// Opens a GitHub issue with the given body text, system info, and optional
    /// crash log content. Returns true if the URL was within length limits.
    /// </summary>
    public static bool OpenFeedbackIssue(
        string title, string body, string? crashLogContent = null,
        Window? owner = null) {
        var info = SystemInfoCollector.Collect(owner);
        var sb = new StringBuilder(body);
        if (crashLogContent is not null) {
            sb.AppendLine();
            sb.AppendLine("<details><summary>Crash Report</summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(crashLogContent);
            sb.AppendLine("```");
            sb.AppendLine("</details>");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(info.ToMarkdownFooter());
        return OpenIssueUrl(title, sb.ToString(), "feedback");
    }

    /// <summary>
    /// Builds a <see cref="FeedbackPayload"/> ready for the Azure Function backend.
    /// Currently unused; will replace the browser URL path when the endpoint is live.
    /// </summary>
    public static FeedbackPayload BuildPayload(
        string type, string message, string? crashLogContent = null,
        Window? owner = null) {
        var payload = SystemInfoCollector.Collect(owner);
        return payload with {
            Type = type,
            Message = message,
            CrashReport = crashLogContent,
        };
    }

    private static bool OpenIssueUrl(string title, string body, string label) {
        var encodedTitle = Uri.EscapeDataString(title);
        var encodedBody = Uri.EscapeDataString(body);
        var encodedLabel = Uri.EscapeDataString(label);

        var url = $"{RepoUrl}/issues/new?title={encodedTitle}&body={encodedBody}&labels={encodedLabel}";

        // Truncate body if URL is too long.
        bool truncated = false;
        if (url.Length > MaxUrlLength) {
            var warning = "\n\n> **Note:** Content was truncated due to URL length limits.";
            var maxBodyChars = body.Length - (url.Length - MaxUrlLength) - warning.Length - 50;
            if (maxBodyChars > 100) {
                body = body[..maxBodyChars] + warning;
                encodedBody = Uri.EscapeDataString(body);
                url = $"{RepoUrl}/issues/new?title={encodedTitle}&body={encodedBody}&labels={encodedLabel}";
            }
            truncated = true;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return !truncated;
    }
}
