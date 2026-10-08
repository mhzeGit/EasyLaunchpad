using Microsoft.Win32;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Launchpad.App.Services;

/// <summary>
/// Only one Launchpad runs. Starting it again forwards the request ("show", or "toggle" with <c>--toggle</c>)
/// to the running copy instead, which makes it easy to bind to other tools or mouse buttons.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle _toggleEvent;
    private readonly Thread? _listener;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _pipeStop = new();
    private Task? _pipeTask;
    private volatile bool _stop;

    public bool IsFirst { get; }
    public event Action? ShowRequested;
    public event Action? ToggleRequested;
    public event Action<string>? AddRequested;

    public SingleInstance()
    {
        string id = Environment.GetEnvironmentVariable("LAUNCHPAD_INSTANCE") ?? "";   // development: lets a test copy run beside the real one
        _pipeName = "Launchpad.Add." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12];
        _mutex = new Mutex(true, @"Local\Launchpad.SingleInstance" + id, out bool created);
        IsFirst = created;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Launchpad.Show" + id);
        _toggleEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Launchpad.Toggle" + id);
        if (IsFirst)
        {
            _listener = new Thread(() =>
            {
                var handles = new WaitHandle[] { _showEvent, _toggleEvent };
                while (!_stop)
                {
                    int i = WaitHandle.WaitAny(handles, 500);
                    if (_stop) break;
                    if (i == 0) ShowRequested?.Invoke();
                    else if (i == 1) ToggleRequested?.Invoke();
                }
            }) { IsBackground = true, Name = "single-instance" };
            _listener.Start();
        }
    }

    public void SignalExisting(bool toggle) => (toggle ? _toggleEvent : _showEvent).Set();

    public void StartAddListener() => _pipeTask ??= Task.Run(AddListenerAsync);

    private async Task AddListenerAsync()
    {
        while (!_pipeStop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_pipeStop.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string path = await reader.ReadToEndAsync(_pipeStop.Token);
                if (!string.IsNullOrWhiteSpace(path)) AddRequested?.Invoke(path);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) when (_pipeStop.IsCancellationRequested) { break; }
            catch (Exception) { if (!_pipeStop.IsCancellationRequested) await Task.Delay(100); }
        }
    }

    public void SignalExistingAdd(string path)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
                pipe.Connect(300);
                byte[] bytes = Encoding.UTF8.GetBytes(path);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                return;
            }
            catch (Exception ex) { last = ex; Thread.Sleep(100); }
        }
        throw new IOException("Could not send the selected item to Launchpad.", last);
    }

    public void Dispose()
    {
        _stop = true;
        _pipeStop.Cancel();
        try { _pipeTask?.Wait(500); } catch (Exception) { }
        _pipeStop.Dispose();
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
        _showEvent.Dispose();
        _toggleEvent.Dispose();
    }
}

public static class ExplorerIntegration
{
    private const string VerbName = "Launchpad.AddToLaunchpad";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        string command = $"\"{exe}\" --add-to-launchpad \"%1\"";
        try
        {
            foreach (string classKey in new[] { @"*", @"Directory" })
            {
                using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{classKey}\shell\{VerbName}");
                verb.SetValue("", "Add to Launchpad");
                verb.SetValue("Icon", exe);
                using var cmd = verb.CreateSubKey("command");
                cmd.SetValue("", command);
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }
        catch (Exception) { /* Explorer integration is best-effort if the per-user registry is unavailable. */ }
    }
}

public static class LoginStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Launchpad";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) key.SetValue(ValueName, $"\"{exe}\" --background");
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
