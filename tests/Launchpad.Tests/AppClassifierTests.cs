using Launchpad.Core.Apps;

namespace Launchpad.Tests;

public class AppClassifierTests
{
    private const string Sys = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\";
    private const string SysX86 = @"{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}\";

    [Theory]
    // admin consoles and system utilities (System32 / SysWOW64 / .msc)
    [InlineData("Event Viewer", Sys + "eventvwr.msc")]
    [InlineData("Component Services", Sys + "comexp.msc")]
    [InlineData("Character Map", Sys + "charmap.exe")]
    [InlineData("Registry Editor", Sys + "regedit.exe")]
    [InlineData("Resource Monitor", Sys + "perfmon.exe")]
    [InlineData("Disk Clean-up", Sys + "cleanmgr.exe")]
    [InlineData("Windows PowerShell (x86)", SysX86 + @"WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("Some Control Panel Applet", Sys + "something.cpl")]
    // developer shells
    [InlineData("Developer Command Prompt for VS", @"C:\Program Files\Microsoft Visual Studio\2022\Common7\Tools\VsDevCmd.bat")]
    [InlineData("x64 Native Tools Command Prompt for VS", @"C:\VS\vcvars64.bat")]
    [InlineData("x86_x64 Cross Tools Command Prompt for VS", @"C:\VS\vcvarsx86_amd64.bat")]
    [InlineData("Node.js command prompt", @"C:\Windows\System32\cmd.exe")]
    [InlineData("Install Additional Tools for Node.js", @"C:\nodejs\install_tools.bat")]
    // documentation, support and web links
    [InlineData("Node.js documentation", "Chrome._crx_abc")]
    [InlineData("Node.js website", "Chrome._crx_def")]
    [InlineData("Python 3.14 Online Documentation", "Chrome._crx_ghi")]
    [InlineData("Python 3.14 Module Docs (64-bit)", @"C:\Python314\python.exe")]
    [InlineData("PyDoc (Python 3.14)", @"C:\Python314\python.exe")]
    [InlineData("Git FAQs (Frequently Asked Questions)", "Chrome._crx_jkl")]
    [InlineData("Steam Support Center", "Chrome._crx_mno")]
    [InlineData("Report a Problem with Unity", @"C:\Unity\Hub\Editor\6000\Editor\UnityBugReporter.exe")]
    [InlineData("Windows Software Development Kit", "Chrome._crx_pqr")]
    // updaters, installers, telemetry, helpers
    [InlineData("Dell Command Update", @"C:\Program Files\Dell\CommandUpdate\DellCommandUpdate.exe")]
    [InlineData("Visual Studio Installer", @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe")]
    [InlineData("Python install manager", @"C:\Python\pymanager.exe")]
    [InlineData("Telemetry Log for Office", @"C:\Program Files\Microsoft Office\root\Office16\msoev.exe")]
    [InlineData("Office Language Preferences", @"C:\Program Files\Microsoft Office\root\Office16\SETLANG.EXE")]
    [InlineData("Power Automate Troubleshooter", "Microsoft.PowerAutomate.Troubleshooter_8wekyb3d8bbwe!App")]
    [InlineData("Get Help", "Microsoft.GetHelp_8wekyb3d8bbwe!App")]
    [InlineData("Windows Tools", "Microsoft.Windows.AdminToolsFolder")]
    public void HidesToolsAndHelpers(string name, string target) => Assert.True(AppClassifier.IsUtility(name, target), name);

    [Theory]
    [InlineData("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")]
    [InlineData("Notepad", Sys + "notepad.exe")]
    [InlineData("Command Prompt", Sys + "cmd.exe")]
    [InlineData("Windows PowerShell", Sys + @"WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("Task Manager", Sys + "taskmgr.exe")]
    [InlineData("Remote Desktop Connection", Sys + "mstsc.exe")]
    [InlineData("Quick Assist", Sys + "quickassist.exe")]
    [InlineData("Snipping Tool", Sys + "SnippingTool.exe")]
    [InlineData("Google Chrome", "Chrome")]
    [InlineData("Google Docs", "Chrome._crx_docs")]            // "docs" alone is not a documentation link
    [InlineData("YouTube", "Chrome._crx_yt")]
    [InlineData("Visual Studio Code", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe")]
    [InlineData("Git Bash", @"C:\Program Files\Git\git-bash.exe")]
    [InlineData("NVIDIA Control Panel", @"C:\Program Files\NVIDIA Corporation\Control Panel Client\nvcplui.exe")]
    [InlineData("Dell SupportAssist", @"C:\Program Files\Dell\SupportAssistAgent\SupportAssist.exe")]
    [InlineData("Steam", @"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData("Unity Hub", @"C:\Program Files\Unity Hub\Unity Hub.exe")]
    [InlineData("Excel", @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE")]
    [InlineData("Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel")]
    public void KeepsRealApplications(string name, string target) => Assert.False(AppClassifier.IsUtility(name, target), name);
}
