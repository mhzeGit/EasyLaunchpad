using System.Text.RegularExpressions;

namespace Launchpad.Core.Apps;

/// <summary>
/// Decides whether an entry of the Start menu's app list is a "utility": something that is installed but that people
/// don't open as an application (admin consoles, command prompts for developers, documentation links, updaters...).
/// Utilities stay out of the launcher grid but remain searchable.
///
/// It is a heuristic on the display name and the shell parsing name (an AppUserModelID, or a path that may start
/// with a known-folder GUID such as <c>{1AC14E77-...}\cmd.exe</c> for System32).
/// </summary>
public static class AppClassifier
{
    // Known-folder GUIDs the shell uses as a path prefix.
    private static readonly string[] SystemFolders =
    {
        "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}",   // System32
        "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}",   // SysWOW64
        "{F38BF404-1D43-42F2-9305-67DE0B28FC23}",   // Windows
    };

    private static readonly string[] AdminToolFolders =
    {
        "{D0384E7D-BAC3-4797-8F14-CBA229B392B5}",   // Common Administrative Tools
        "{724EF170-A42D-4FEF-9F26-B60E846FBA4F}",   // Administrative Tools
    };

    /// <summary>Programs in the system folders that people genuinely use.</summary>
    private static readonly HashSet<string> SystemAppsToKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad.exe", "mspaint.exe", "calc.exe", "cmd.exe", "powershell.exe", "taskmgr.exe", "snippingtool.exe",
        "mstsc.exe", "wordpad.exe", "write.exe", "explorer.exe", "control.exe", "magnify.exe", "narrator.exe", "osk.exe",
        "wt.exe", "pwsh.exe", "quickassist.exe", "livecaptions.exe", "voiceaccess.exe",
    };

    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get Help", "Get Started", "Tips", "Feedback Hub", "Windows Tools", "Spreadsheet Compare", "Database Compare",
        "Windows PowerShell ISE", "Windows PowerShell (x86)", "Windows PowerShell ISE (x86)", "Registry Editor",
        "Recovery Drive", "Task Scheduler", "Services", "System Information", "System Configuration",
        "Component Services", "Computer Management", "Event Viewer", "Performance Monitor", "Resource Monitor",
        "ODBC Data Sources (32-bit)", "ODBC Data Sources (64-bit)", "iSCSI Initiator", "Windows Memory Diagnostic",
        "Character Map", "Steps Recorder", "Print Management", "Disk Clean-up", "Disk Cleanup", "Defragment and Optimise Drives",
        "Defragment and Optimize Drives", "Local Security Policy", "Windows Fax and Scan",
        "Application Verifier (WOW)", "Application Verifier (X64)", "Application Verifier (X86)", "Math Input Panel",
    };

    private static readonly Regex[] JunkPatterns = Build(
        // developer shells and tooling entry points
        @"\b(developer|native tools|cross tools)\b.*\b(command prompt|powershell|prompt)\b",
        @"^node\.js command prompt$",
        @"^install additional tools\b",
        @"^pydoc\b",
        @"^report a problem\b",
        @"\b(development kit|cert(ification)? kit)\b",
        // documentation, help and web links
        @"\b(documentation|manuals?|module docs|faqs?|release notes|readme|user guide|knowledge base|support center|web ?site)\b",
        @"\bfrequently asked\b",
        // installers, updaters, diagnostics, settings helpers
        @"\b(uninstall|uninstaller|repair|updater|check for updates|telemetry|diagnostics?|troubleshooter)\b",
        @"\blanguage preferences\b",
        @"\bupdate$",
        @"\binstall manager$",
        @"\binstaller$");

    private static Regex[] Build(params string[] patterns) =>
        patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)).ToArray();

    public static bool IsUtility(string name, string target)
    {
        if (JunkNames.Contains(name)) return true;
        foreach (var rx in JunkPatterns)
            if (rx.IsMatch(name)) return true;

        string t = target.Replace('/', '\\');

        foreach (string g in AdminToolFolders)
            if (t.StartsWith(g, StringComparison.OrdinalIgnoreCase)) return true;

        // management consoles and control-panel applets are never "applications"
        if (t.EndsWith(".msc", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase)) return true;

        string exe = Path.GetFileName(t);
        bool inSystemFolder = SystemFolders.Any(g => t.StartsWith(g, StringComparison.OrdinalIgnoreCase)) || IsUnderWindowsDir(t);
        if (inSystemFolder && !SystemAppsToKeep.Contains(exe)) return true;

        return false;
    }

    private static bool IsUnderWindowsDir(string path)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
