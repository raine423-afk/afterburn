using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NexusMods.Sdk;

namespace NexusMods.App;

/// <summary>
/// Afterburn: when the app crashes, saves a plain-text crash log (error + recent log lines) to the crash log folder
/// and offers to copy it so the user can send it to the developer. Nothing is sent automatically.
/// </summary>
internal static class CrashReporter
{
    private static string _folder = "";
    private static string _logFile = "";
    private static int _handling;

    public static void Install(string folder, string mainLogFile)
    {
        _folder = folder;
        _logFile = mainLogFile;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Handle(e.ExceptionObject as Exception);
    }

    private static void Handle(Exception? exception)
    {
        if (Interlocked.Exchange(ref _handling, 1) == 1) return;
        try
        {
            try { NLog.LogManager.Flush(TimeSpan.FromSeconds(2)); } catch { /* ignored */ }

            var text = BuildReport(exception);
            var folder = _folder;
            try { Directory.CreateDirectory(folder); }
            catch
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(folder);
            }

            var path = Path.Combine(folder, $"Afterburn-crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));

            if (!OperatingSystem.IsWindows()) return;

            var message =
                "Afterburn closed unexpectedly.\n\n" +
                "A crash log was saved to:\n" + path + "\n\n" +
                "You can change this folder in Afterburn's Settings (Crash Log Folder).\n\n" +
                "Would you like to send the crash log to the developer?\n" +
                "Yes: copies the log and opens the Afterburn issues page, where you paste it into a new issue.\n" +
                "No: just close. Nothing is sent unless you choose Yes.";
            var answer = MessageBoxW(IntPtr.Zero, message, "Afterburn crashed", 0x4 | 0x10 | 0x40000);
            if (answer != 6) return;

            CopyToClipboard(text);
            var url = AfterburnBrand.IssuesUri.ToString().TrimEnd('/') + "/new?title=" + Uri.EscapeDataString("Crash report");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // never throw from the crash handler
        }
    }

    private static string BuildReport(Exception? exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Afterburn crash log");
        sb.AppendLine("Time: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        sb.AppendLine("Version: " + ApplicationConstants.Version + " (" + (ApplicationConstants.CommitHash ?? "unknown commit") + ")");
        sb.AppendLine("OS: " + RuntimeInformation.OSDescription);
        sb.AppendLine("Runtime: " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine();
        sb.AppendLine("=== Error ===");
        sb.AppendLine(exception?.ToString() ?? "(no exception object)");
        sb.AppendLine();
        sb.AppendLine("=== Last log lines ===");
        try
        {
            using var stream = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var queue = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                if (queue.Count > 200) queue.Dequeue();
            }
            foreach (var line in queue) sb.AppendLine(line);
        }
        catch (Exception e)
        {
            sb.AppendLine("(could not read the log file: " + e.GetType().Name + ")");
        }

        return Redact(sb.ToString());
    }

    // Keep the user name out of the log.
    private static string Redact(string text)
    {
        try
        {
            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user)) text = Regex.Replace(text, Regex.Escape(user), "<user>", RegexOptions.IgnoreCase);
        }
        catch { /* ignored */ }
        return text;
    }

    private static void CopyToClipboard(string text)
    {
        var psi = new ProcessStartInfo("clip.exe")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UnicodeEncoding(false, true),
        };
        using var process = Process.Start(psi);
        if (process is null) return;
        process.StandardInput.Write(text);
        process.StandardInput.Close();
        process.WaitForExit(5000);
    }

    internal static void ShowMessage(string text, string caption)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { MessageBoxW(IntPtr.Zero, text, caption, 0x30 | 0x40000); } catch { /* ignored */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}