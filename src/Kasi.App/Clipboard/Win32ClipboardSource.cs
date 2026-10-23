using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kasi.App.Clipboard;

/// <summary>
/// The real clipboard source: a message-only window on its own thread that asks Windows (<c>AddClipboardFormatListener</c>) to tell it when
/// the clipboard changes. It exists only between <see cref="Start"/> and <see cref="Stop"/>, so with the setting off no listener is
/// registered. Reading takes the Unicode text format only and never touches other formats.
/// </summary>
internal sealed unsafe partial class Win32ClipboardSource : IClipboardSource, IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint CfUnicodeText = 13;
    private const long MaxChars = 16 * 1024; // far more than any address; a bigger paste is not a link

    private static readonly nint _messageOnly = -3;
    private static Win32ClipboardSource? _current; // the window procedure is static; one source is active at a time

    private readonly Lock _gate = new();
    private Thread? _thread;
    private nint _window;

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            using var ready = new ManualResetEventSlim();
            _current = this;
            _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Kasi clipboard listener" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
        }
    }

    public void Stop()
    {
        Thread? thread;
        nint window;
        lock (_gate)
        {
            thread = _thread;
            window = _window;
            _thread = null;
        }

        if (thread is null)
        {
            return;
        }

        if (window != 0)
        {
            _ = NativeMethods.PostMessageW(window, WmClose, 0, 0);
        }

        thread.Join();
    }

    public void Dispose() => Stop();

    public string? ReadText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                try
                {
                    if (NativeMethods.IsClipboardFormatAvailable(CfUnicodeText) == 0)
                    {
                        return null;
                    }

                    var handle = NativeMethods.GetClipboardData(CfUnicodeText);
                    if (handle == 0)
                    {
                        return null;
                    }

                    var size = (long)NativeMethods.GlobalSize(handle);
                    var locked = NativeMethods.GlobalLock(handle);
                    if (locked == 0)
                    {
                        return null;
                    }

                    try
                    {
                        var chars = Math.Min(size / sizeof(char), MaxChars + 1);
                        var text = new string((char*)locked, 0, (int)chars);
                        var end = text.IndexOf('\0', StringComparison.Ordinal);
                        return end >= 0 ? text[..end] : text;
                    }
                    finally
                    {
                        _ = NativeMethods.GlobalUnlock(handle);
                    }
                }
                finally
                {
                    _ = NativeMethods.CloseClipboard();
                }
            }

            Thread.Sleep(20); // another program has the clipboard open for a moment
        }

        return null;
    }

    private void Run(ManualResetEventSlim ready)
    {
        var className = "KasiClipboardListener-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        fixed (char* name = className)
        {
            var wndClass = new NativeMethods.WndClassEx
            {
                Size = (uint)sizeof(NativeMethods.WndClassEx),
                WindowProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
                ClassName = (nint)name,
            };
            if (NativeMethods.RegisterClassExW(ref wndClass) == 0)
            {
                ready.Set();
                return;
            }

            _window = NativeMethods.CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0, _messageOnly, 0, 0, 0);
            if (_window != 0 && NativeMethods.AddClipboardFormatListener(_window) == 0)
            {
                _ = NativeMethods.DestroyWindow(_window);
                _window = 0;
            }

            ready.Set();
            if (_window != 0)
            {
                while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
                {
                    _ = NativeMethods.TranslateMessage(ref message);
                    _ = NativeMethods.DispatchMessageW(ref message);
                }
            }

            _ = NativeMethods.UnregisterClassW(className, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmClipboardUpdate:
                try
                {
                    _current?.Changed?.Invoke();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // a handler fault must never unwind into Windows
                }

                return 0;
            case WmClose:
                _ = NativeMethods.RemoveClipboardFormatListener(window);
                _ = NativeMethods.DestroyWindow(window);
                return 0;
            case WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return 0;
            default:
                return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private static unsafe partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public nint WindowProc;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public nint MenuName;
            public nint ClassName;
            public nint IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Msg
        {
            public nint Window;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static partial ushort RegisterClassExW(ref WndClassEx wndClass);

        [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnregisterClassW(string className, nint instance);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint window);

        [LibraryImport("user32.dll")]
        internal static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        internal static partial int GetMessageW(out Msg message, nint window, uint min, uint max);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TranslateMessage(ref Msg message);

        [LibraryImport("user32.dll")]
        internal static partial nint DispatchMessageW(ref Msg message);

        [LibraryImport("user32.dll")]
        internal static partial void PostQuitMessage(int exitCode);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        internal static partial int AddClipboardFormatListener(nint window);

        [LibraryImport("user32.dll")]
        internal static partial int RemoveClipboardFormatListener(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenClipboard(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseClipboard();

        [LibraryImport("user32.dll")]
        internal static partial int IsClipboardFormatAvailable(uint format);

        [LibraryImport("user32.dll")]
        internal static partial nint GetClipboardData(uint format);

        [LibraryImport("kernel32.dll")]
        internal static partial nuint GlobalSize(nint memory);

        [LibraryImport("kernel32.dll")]
        internal static partial nint GlobalLock(nint memory);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GlobalUnlock(nint memory);
    }
}
