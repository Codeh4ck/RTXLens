using System.Runtime.InteropServices;
using RTXLens.App.Platform.Interop;
#if !AVALONIA_DESKTOP
using Microsoft.UI.Dispatching;
using RTXLens.App.Platform.Windowing;
#endif

namespace RTXLens.App.Platform.Tray;

internal sealed partial class TrayService : IDisposable
{
    private const uint CallbackMessage = 0x800B;
    private readonly nint _window;
    private readonly Action<Action> _dispatch;
    private readonly Action _show;
    private readonly Action _settings;
    private readonly Action _exit;
    private readonly Action<bool> _minimizeChanged;
    private readonly nint _previous;
    private readonly uint _taskbarCreated;
    private readonly uint _showMessage;
    private GCHandle _procedureHandle;
    private NotifyIconData _data;
    private bool _disposed;
    public bool IsAvailable { get; private set; }

#if !AVALONIA_DESKTOP
    public TrayService(nint window, DispatcherQueue dispatcher, Action show, Action settings, Action exit, Action<bool> minimizeChanged)
        : this(window, action => dispatcher.TryEnqueue(() => action()), show, settings, exit, minimizeChanged)
    {
    }
#endif

    public TrayService(nint window, Action<Action> dispatch, Action show, Action settings, Action exit, Action<bool> minimizeChanged)
    {
        _window = window;
        _dispatch = dispatch;
        _show = show;
        _settings = settings;
        _exit = exit;
        _minimizeChanged = minimizeChanged;
        _taskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _showMessage = NativeMethods.RegisterWindowMessageW(DesktopBuild.ShowMessageName);

        WindowProcedure procedure = ProcessMessage;

        // Windows retains a function pointer; root the delegate until the window subclass is removed.
        _procedureHandle = GCHandle.Alloc(procedure);
        try
        {
            _previous = NativeMethods.SetWindowLongPtr(window, NativeConstants.WindowProcedureIndex, Marshal.GetFunctionPointerForDelegate(procedure));
            if (_previous == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            _procedureHandle.Free();
            throw;
        }

        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = window,
            Id = 1,
            Flags = NativeConstants.NotifyMessageFlag | NativeConstants.NotifyIconFlag | NativeConstants.NotifyTipFlag | NativeConstants.NotifyGuidFlag | NativeConstants.NotifyShowTipFlag,
            CallbackMessage = CallbackMessage,
            Icon = NativeMethods.LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "Assets", "monitor.ico"), NativeConstants.IconImage, 32, 32, NativeConstants.LoadFromFile),
            Tip = DesktopBuild.WindowTitle + " · connecting",
            Info = string.Empty,
            InfoTitle = string.Empty,
            Guid = DesktopBuild.TrayIconId
        };

        AddIcon();
    }

    public void SetStatus(string status)
    {
        var tip = DesktopBuild.WindowTitle + " · " + status;
        _data.Tip = tip[..Math.Min(tip.Length, 127)];
        NativeMethods.Shell_NotifyIconW(NativeConstants.NotifyModify, ref _data);
    }

    public void Notify(string title, string message)
    {
        _data.InfoTitle = title[..Math.Min(title.Length, 63)];
        _data.Info = message[..Math.Min(message.Length, 255)];
        _data.InfoFlags = NativeConstants.BalloonWarning | NativeConstants.RespectQuietTime;
        _data.Flags |= NativeConstants.NotifyInfoFlag;

        NativeMethods.Shell_NotifyIconW(NativeConstants.NotifyModify, ref _data);

        _data.Flags &= ~NativeConstants.NotifyInfoFlag;
        _data.Info = string.Empty;
        _data.InfoTitle = string.Empty;
    }

    private void AddIcon()
    {
        IsAvailable = NativeMethods.Shell_NotifyIconW(NativeConstants.NotifyAdd, ref _data);
        _data.Version = 4;

        NativeMethods.Shell_NotifyIconW(NativeConstants.NotifySetVersion, ref _data);
    }

    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam)
    {
#if !AVALONIA_DESKTOP
        if (message == NativeConstants.GetMinMaxInfo)
        {
            var result = NativeMethods.CallWindowProcW(_previous, window, message, wParam, lParam);
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var scale = Math.Max(96, NativeMethods.GetDpiForWindow(window)) / 96d;
            var maximum = WindowSizeLimits.GetMaximumPixels(window);

            limits.MinTrackSize = new NativePoint
            {
                X = Math.Min(maximum.Width, (int)(WindowSizeLimits.MinimumWidth * scale)),
                Y = Math.Min(maximum.Height, (int)(WindowSizeLimits.MinimumHeight * scale))
            };

            limits.MaxTrackSize = new NativePoint { X = maximum.Width, Y = maximum.Height };
            limits.MaxSize = limits.MaxTrackSize;

            Marshal.StructureToPtr(limits, lParam, false);

            return result;
        }

#endif
        if (message == _taskbarCreated)
        {
            AddIcon();
        }

        if (message == _showMessage)
        {
            _dispatch(() => _show());
        }

        switch (message)
        {
            case NativeConstants.WindowSize:
                _dispatch(() => _minimizeChanged(wParam == 1));
                break;
            case CallbackMessage:
                {
                    var action = (uint)(lParam.ToInt64() & 0xFFFF);
                    switch (action)
                    {
                        case NativeConstants.LeftDoubleClick or NativeConstants.TraySelect or NativeConstants.TrayKeyboardSelect or NativeConstants.BalloonClick:
                            _dispatch(() => _show());
                            break;
                        case NativeConstants.ContextMenu or NativeConstants.RightButtonUp:
                            ShowMenu();
                            break;
                    }

                    break;
                }
        }

        return NativeMethods.CallWindowProcW(_previous, window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        try
        {
            NativeMethods.AppendMenuW(menu, 0, NativeConstants.CommandOpen, "Open RTX Lens");
            NativeMethods.AppendMenuW(menu, 0, NativeConstants.CommandSettings, "Settings");
            NativeMethods.AppendMenuW(menu, NativeConstants.MenuSeparator, 0, null);
            NativeMethods.AppendMenuW(menu, 0, NativeConstants.CommandExit, "Exit");
            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_window);

            var command = NativeMethods.TrackPopupMenu(menu, NativeConstants.MenuReturnCommand | NativeConstants.MenuRightButton, point.X, point.Y, 0, _window, 0);

            switch (command)
            {
                case NativeConstants.CommandOpen:
                    _dispatch(() => _show());
                    break;
                case NativeConstants.CommandSettings:
                    _dispatch(() => _settings());
                    break;
                case NativeConstants.CommandExit:
                    _dispatch(() => _exit());
                    break;
            }

            NativeMethods.PostMessageW(_window, 0, 0, 0);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        NativeMethods.Shell_NotifyIconW(NativeConstants.NotifyDelete, ref _data);
        NativeMethods.SetWindowLongPtr(_window, NativeConstants.WindowProcedureIndex, _previous);

        _procedureHandle.Free();

        if (_data.Icon != 0)
        {
            NativeMethods.DestroyIcon(_data.Icon);
        }
    }
}
