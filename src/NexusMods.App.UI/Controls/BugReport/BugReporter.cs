using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.CrossPlatform;

namespace NexusMods.App.UI.Controls.BugReport;

/// <summary>
/// One-click bug reporting: builds a short diagnostic report (version, OS and the end of the log, with personal
/// details removed), copies it to the clipboard, and opens a pre-filled GitHub issue. Nothing is sent anywhere
/// automatically; the user reviews and pastes the report themselves.
/// </summary>
public static class BugReporter
{
    private const int MaxLogLines = 150;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SecretRegex = new(
        @"(?i)(api[-_ ]?key|token|authorization|bearer|password|secret)([""'=:\s]+)[A-Za-z0-9+/=_\-\.]{16,}",
        RegexOptions.None, RegexTimeout);

    private static readonly Regex EmailRegex = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.None, RegexTimeout);

    // Afterburn: Nexus download links carry a personal, time-limited key ("?key=...&expires=...").
    private static readonly Regex QueryKeyRegex = new(
        @"(?i)([?&](?:key|expires|user_id|token|access_token|refresh_token|code|state)=)[^&\s""'`]+",
        RegexOptions.None, RegexTimeout);

    public static async Task ReportAsync(
        IAvaloniaInterop avaloniaInterop,
        IOSInterop osInterop,
        ISettingsManager settingsManager,
        IFileSystem fileSystem,
        ILogger logger)
    {
        string report;
        try
        {
            var logPath = settingsManager.Get<LoggingSettings>().MainProcessLogFilePath.ToPath(fileSystem);
            report = BuildReport(logPath.ToString());
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to build the bug report");
            report = "(Afterburn could not read its log file. Please describe what happened.)";
        }

        try
        {
            await avaloniaInterop.SetClipboardTextAsync(report);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to copy the bug report to the clipboard");
        }

        var body = "**What happened?**\n\n\n**What did you expect to happen?**\n\n\n**Diagnostic report**\nPaste it here with Ctrl+V. Afterburn already copied it for you, and removed your user name, paths, e-mail addresses and keys. Please look it over before posting.\n";
        var uri = new Uri(AfterburnBrand.IssuesUri + "/new?title=" + Uri.EscapeDataString("Bug: ") + "&body=" + Uri.EscapeDataString(body));
        osInterop.OpenUri(uri);
    }

    private static string BuildReport(string logPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine($"Afterburn {ApplicationConstants.Version}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Memory in use: {Environment.WorkingSet / (1024 * 1024)} MB");
        sb.AppendLine($"Time (UTC): {DateTime.UtcNow:u}");
        sb.AppendLine();
        sb.AppendLine($"--- last {MaxLogLines} log lines ---");

        foreach (var line in ReadTail(logPath, MaxLogLines)) sb.AppendLine(Redact(line));

        sb.AppendLine("```");
        return sb.ToString();
    }

    private static IEnumerable<string> ReadTail(string path, int count)
    {
        var queue = new Queue<string>(count + 1);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                if (queue.Count > count) queue.Dequeue();
            }
        }
        catch (Exception)
        {
            queue.Enqueue("(log file not available)");
        }

        return queue;
    }

    internal static string Redact(string text)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) text = text.Replace(home, "<home>", StringComparison.OrdinalIgnoreCase);
            // Replace the user and PC names only as whole words, so a short name doesn't mangle unrelated text.
            text = ReplaceWord(text, Environment.UserName, "<user>");
            text = ReplaceWord(text, Environment.MachineName, "<pc>");
            text = QueryKeyRegex.Replace(text, "$1<removed>");
            text = SecretRegex.Replace(text, "$1$2<removed>");
            text = EmailRegex.Replace(text, "<email>");
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            return "(line removed: could not be cleaned safely)";
        }
    }

    private static string ReplaceWord(string text, string? word, string replacement)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 2) return text;
        return Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])", replacement,
            RegexOptions.IgnoreCase, RegexTimeout);
    }
}
