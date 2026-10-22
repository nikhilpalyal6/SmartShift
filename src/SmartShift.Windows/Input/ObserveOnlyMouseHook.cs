using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SmartShift.Core.Logging;
using SmartShift.Windows.DragDrop;

namespace SmartShift.Windows.Input;

/// <summary>
/// Optional WH_MOUSE_LL observer. Always calls the next hook and never cancels input.
/// Does not read OLE drag payloads. Disabled unless explicitly started.
/// </summary>
public sealed class ObserveOnlyMouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmMButtonDown = 0x0207;
    private const int HcAction = 0;

    private readonly DiagnosticLogger _logger;
    private readonly NativeMethods.HookProc _proc;
    private IntPtr _hook;
    private bool _disposed;

    public ObserveOnlyMouseHook(DiagnosticLogger logger)
    {
        _logger = logger;
        _proc = HookCallback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public event Action<ModifierKeySnapshot, string>? Observed;

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ObserveOnlyMouseHook));
        }

        if (IsInstalled)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandle(module?.ModuleName ?? string.Empty);
        _hook = NativeMethods.SetWindowsHookEx(WhMouseLl, _proc, moduleHandle, 0);
        if (_hook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.Error("hook", $"SetWindowsHookEx failed with Win32 error {error}.");
            throw new InvalidOperationException($"Failed to install observe-only mouse hook. Win32 error {error}.");
        }

        _logger.Info("hook", "Installed observe-only WH_MOUSE_LL hook. Events are forwarded; OLE payloads are not available.");
    }

    public void Stop()
    {
        if (!IsInstalled)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _logger.Info("hook", "Removed observe-only mouse hook.");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == HcAction)
            {
                var message = wParam.ToInt32();
                if (message is WmLButtonDown or WmMButtonDown)
                {
                    var snapshot = NativeInput.CaptureModifiers();
                    var kind = message == WmMButtonDown ? "MMB" : "LMB";
                    var dispatcher = Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
                    dispatcher.BeginInvoke(() =>
                    {
                        Observed?.Invoke(snapshot, kind);
                        _logger.Info("hook", $"Observed {kind} without OLE data.", new Dictionary<string, string>
                        {
                            ["modifiers"] = snapshot.Describe()
                        });
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("hook", "Observer callback failed; input still forwarded.", ex);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static class NativeMethods
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
