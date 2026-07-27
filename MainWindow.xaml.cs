using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;
using static uTimer.WindowsSystemDispatcherQueueHelper;

namespace uTimer;

/// <summary>
/// Main application window for uTimer, a borderless, anlways-on-top time tracker.
/// Tracks active foreground programs and pauses automatically when system idle time exceeds a threshold.
/// </summary>

public sealed partial class MainWindow : Window
{
    // Backdrop and Acrylic Controller for modern Windows visual effects.
    private WindowsSystemDispatcherQueueHelper _dispatcherQueueHelper;
    private DesktopAcrylicController _acrylicController;
    private SystemBackdropConfiguration _configurationSource;

    // Core window and timer management.
    private AppWindow _appWindow;
    private DispatcherTimer _timer;
    private TimeSpan _elapsedTime;

    // Program Slots: stores up to 3 process names to track for activity.
    private string[] _targetPrograms = new string[3] { "", "", "" };
    private int _waitingForSlot = -1;

    // Setting Variables: User Configuration Variables with default values.
    private string _currentTheme = "Dark";
    private bool _isLocked = false;
    private double _currentOpacity = 0.65;
    private string _currentFontFamily = "WindowsDefault";
    private int _windowX = int.MinValue;
    private int _windowY = int.MinValue;

    // ==============================
    // UI Loaded Event Handlers
    // ==============================

    /// <summary>
    /// Syncs the ThemeComboBox selection with the loaded setting upon initialization.
    /// </summary>
    private void ThemeComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb)
        {
            foreach (ComboBoxItem item in cb.Items)
            {
                if (item.Tag as string == _currentTheme)
                {
                    cb.SelectedItem = item;
                    break;
                }
            }
        }
    }
    /// <summary>
    /// Syncs the OpactivyNumberBox value with the loaded setting without triggering change events.
    /// </summary>
    private void OpacityNumberBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox nb)
        {
            _suppressOpacityValueChanged = true;
            nb.Value = _currentOpacity;
            _suppressOpacityValueChanged = false;
        }
    }

    /// <summary>
    /// Syncs the TimeoutNumberBox value with the loaded setting upon initialization.
    /// </summary>
    private void TimeoutNumberBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox nb)
        {
            nb.Value = _timeoutSeconds;
        }
    }

    // ==========================================
    // Custom Drag Handling State
    // ==========================================
    private IntPtr _hWnd;
    private bool _leftPressed = false;
    private bool _isDragging = false;
    private POINT _pressCursor;
    private RECT _pressWindowRect;
    private int _dragThresholdX;
    private int _dragThresholdY;
    private bool _suppressOpacityValueChanged = false;

    // ==========================================
    // Win32 P/Invoke Declarations[cite: 1]
    // ==========================================
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll", EntryPoint = "SetWindowRgn")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // DWM Window Attribute Constants
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    // Window Message Constants
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    // Window Style Constants
    private const int GWL_STYLE = -16;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;

    // SetWindowPos Flags
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// Attempts to apply an always-active Desktop Acrylic backdrop to the window.
    /// </summary>
    private bool TrySetAlwaysActiveAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
            return false;

        // Ensure COM dispatcher queue exists for the current thread.
        _dispatcherQueueHelper = new WindowsSystemDispatcherQueueHelper();
        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();

        _configurationSource = new SystemBackdropConfiguration();
        this.Activated += Window_Activated;
        this.Closed += Window_Closed;
        _configurationSource.IsInputActive = true;

        _acrylicController = new DesktopAcrylicController();
        _acrylicController.TintOpacity = 0.4f;
        _acrylicController.LuminosityOpacity = 0.3f;
        _acrylicController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_configurationSource);

        return true;
    }

    /// <summary>
    /// Ensures system backdrop remains visual active when window activation state changes.
    /// </summary>
    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_configurationSource != null)
        {
            _configurationSource.IsInputActive = true;
        }
    }

    // ==========================================
    // Settings Persistence
    // ==========================================
    private void SaveSettings()
    {
        // 1. Save Program Slot
        for (int i = 0; i < 3; i++)
        {
            IniHelper.Write("Programs", $"Prog{i}", _targetPrograms[i]);
        }

        // 2. Save User Settings
        IniHelper.Write("Settings", "TimeoutSeconds", _timeoutSeconds.ToString());
        IniHelper.Write("Settings", "Theme", _currentTheme);
        IniHelper.Write("Settings", "IsLocked", _isLocked.ToString());
        IniHelper.Write("Settings", "Opacity", _currentOpacity.ToString());
        IniHelper.Write("Settings", "FontFamily", _currentFontFamily);

        // 3. Save Current Window Position if valid and not minimized
        if (_hWnd != IntPtr.Zero && GetWindowRect(_hWnd, out RECT rect))
        {
            if (rect.Left > -10000 && rect.Top > -10000)
            {
                IniHelper.Write("Settings", "WindowX", rect.Left.ToString());
                IniHelper.Write("Settings", "WindowY", rect.Top.ToString());
            }
        }
    }

    /// <summary>
    /// Reads settings from local INI storage and restores window state and target programs.
    /// </summary>
    private void LoadSettings()
    {
        // 1. Load Program Slot
        for (int i = 0; i < 3; i++)
        {
            _targetPrograms[i] = IniHelper.Read("Programs", $"Prog{i}", "");
        }
        UpdateMenuUI(); // Sync Menu UI

        // 2. Load User Settings
        _timeoutSeconds = IniHelper.ReadInt("Settings", "TimeoutSeconds", 3);
        _currentTheme = IniHelper.Read("Settings", "Theme", "Dark");
        _currentOpacity = IniHelper.ReadDouble("Settings", "Opacity", 0.65);
        _currentFontFamily = IniHelper.Read("Settings", "FontFamily", "WindowsDefault");

        _isLocked = IniHelper.ReadBool("Settings", "IsLocked", false);
        MenuLock.IsChecked = _isLocked;

        // 3. Load Window Position
        int startX = IniHelper.ReadInt("Settings", "WindowX", int.MinValue);
        int startY = IniHelper.ReadInt("Settings", "WindowY", int.MinValue);
        if (startX != int.MinValue && startY != int.MinValue)
        {
            _windowX = startX;
            _windowY = startY;
        }
    }

    /// <summary>
    /// Handles cleanup, settings saving, and WndProc restoration when closing the program.
    /// </summary>
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        SaveSettings();

        // Restore original window procedure before destroying the window.
        if (_hWnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hWnd, GWLP_WNDPROC, _oldWndProc);
        }

        _acrylicController?.Dispose();
        _acrylicController = null;
        _configurationSource = null;
    }

    // ==========================================
    // Constructor & Initialization
    // ==========================================
    public MainWindow()
    {
        this.InitializeComponent();

        // Retrieve native HWND and associate with AppWindow.
        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Resize window to compact square footprint.
        int windowWidth = 195;
        int windowHeight = 195;
        _appWindow.Resize(new Windows.Graphics.SizeInt32(windowWidth, windowHeight));

        // Configure window styles: borderless, always-on-top, non-resizable.
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;

            try
            {
                // Strip native window decorations using Win32 API.
                IntPtr style = GetWindowLongPtr(hWnd, GWL_STYLE);
                style = (IntPtr)(style.ToInt64() & ~(WS_BORDER | WS_DLGFRAME | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU));
                SetWindowLongPtr(hWnd, GWL_STYLE, style);
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            }
            catch { }
        }

        TrySetAlwaysActiveAcrylicBackdrop();

        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Subclass window to intercept hit-testing for custom dragging.
        SubclassWindow(_hWnd);

        try
        {
            // Apply Desktop Window Manager (DWM) styles for border colors and clipping regions.
            int cornerPref = 1; // DoNotRound (We apply our own custom round region below).
            DwmSetWindowAttribute(_hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
            int borderColor = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(_hWnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            // Apply 24px corner radius clipping region.
            int cornerRadius = 24;
            IntPtr hRgn = CreateRoundRectRgn(0, 0, windowWidth + 1, windowHeight + 1, cornerRadius, cornerRadius);
            SetWindowRgn(_hWnd, hRgn, true);
        }
        catch { }

        // Get system metrics to determine pointer drag thresholds.
        _dragThresholdX = GetSystemMetrics(SM_CXDRAG);
        _dragThresholdY = GetSystemMetrics(SM_CYDRAG);

        LoadSettings();
        if (_windowX != int.MinValue && _windowY != int.MinValue)
        {
            _appWindow.Move(new Windows.Graphics.PointInt32(_windowX, _windowY));
        }
        ApplyTheme();
        ApplyFontFamily();

        _elapsedTime = TimeSpan.Zero;

        try { _baseSubText = SubText?.Text ?? string.Empty; } catch { _baseSubText = string.Empty; }

        // Initialize 1-second interval timer for tracking active usage.
        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    // ==========================================
    // Window Subclassing & Hit Testing
    // ==========================================
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate _wndProcDelegate;
    private IntPtr _oldWndProc = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    const int SM_CXDRAG = 68;
    const int SM_CYDRAG = 69;

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    static extern ulong GetTickCount64();

    private int _timeoutSeconds = 3;
    private bool _isTimedOut = false;
    private string _baseSubText = "";

    /// <summary>
    /// Replaces the native window procedure with a custom callback.
    /// </summary>
    private void SubclassWindow(IntPtr hWnd)
    {
        _wndProcDelegate = new WndProcDelegate(CustomWndProc);
        IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _oldWndProc = SetWindowLongPtr(hWnd, GWLP_WNDPROC, ptr);
    }

    /// <summary>
    /// Custom WndProc that hijacks WM_NCHITTEST to allow dragging the window by clicking anywhere when unlocked.
    /// </summary>
    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST)
        {
            if (!_isLocked)
            {
                IntPtr result = CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
                // If the OS considers this a standard client area hit, treat it as HTCAPTION to enable dragging.
                if (result == (IntPtr)1)
                {
                    return HTCAPTION_VAL;
                }
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll", EntryPoint = "CallWindowProc", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x0084;
    private const IntPtr HTCAPTION_VAL = (IntPtr)2;

    // ==========================================
    // Custom Pointer Dragging Implementation
    // ==========================================
    private void OnDragAreaPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(null);
        if (pt.Properties.IsRightButtonPressed) return;

        if (pt.Properties.IsLeftButtonPressed)
        {
            _leftPressed = true;
            GetCursorPos(out _pressCursor);
            GetWindowRect(_hWnd, out _pressWindowRect);
            if (sender is UIElement ui) ui.CapturePointer(e.Pointer);
            _isDragging = false;
            e.Handled = true;
        }
    }

    private void OnDragAreaPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Block Window Moving When Locked
        if (_isLocked) return;

        if (!_leftPressed) return;

        GetCursorPos(out POINT current);
        int dx = Math.Abs(current.X - _pressCursor.X);
        int dy = Math.Abs(current.Y - _pressCursor.Y);

        if (!_isDragging)
        {
            // Verify pointer moved past system drag threshold before initiating window move.
            if (dx >= _dragThresholdX || dy >= _dragThresholdY)
            {
                _isDragging = true;
            }
            else
            {
                return;
            }
        }
        // Calculate offset and reposition window via SetWindowPos.
        int newX = _pressWindowRect.Left + (current.X - _pressCursor.X);
        int newY = _pressWindowRect.Top + (current.Y - _pressCursor.Y);
        SetWindowPos(_hWnd, IntPtr.Zero, newX, newY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
        e.Handled = true;
    }

    private void OnDragAreaPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_leftPressed)
        {
            _leftPressed = false;
            _isDragging = false;
            if (sender is UIElement ui) ui.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Displays context menu flyout upon right-clicking the drag area.
    /// </summary>
    private void OnDragAreaRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (RootBorder != null && RootBorder.ContextFlyout is MenuFlyout flyout)
        {
            flyout.ShowAt(RootBorder, e.GetPosition(RootBorder));
            e.Handled = true;
        }
    }

    // ==========================================
    // Idle & Active Process Monitoring
    // ==========================================

    /// <summary>
    /// Checks system-wide user input idle time using GetLastInputInfo.
    /// Sets _isTimedOut to true if idle time exceeds configured timeout.
    /// </summary>
    private void UpdateIdleState()
    {
        if (_timeoutSeconds <= 0)
        {
            _isTimedOut = false;
            return;
        }

        LASTINPUTINFO li = new LASTINPUTINFO();
        li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        li.dwTime = 0;
        if (!GetLastInputInfo(ref li))
        {
            _isTimedOut = false;
            return;
        }

        ulong lastInputMs = li.dwTime;
        ulong tick = GetTickCount64();
        ulong idleMs = (tick >= lastInputMs) ? (tick - lastInputMs) : 0UL;
        _isTimedOut = idleMs >= (ulong)(_timeoutSeconds * 1000);
    }

    private void TimeoutNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!sender.IsLoaded) return;

        double val = sender.Value;
        int intVal = (int)Math.Round(val);
        if (intVal < 0) intVal = 0;
        if (intVal > 3600) intVal = 3600;

        if (Math.Abs(sender.Value - intVal) > 0.0)
        {
            try { sender.Value = intVal; } catch { }
        }

        _timeoutSeconds = intVal;
    }

    /// <summary>
    /// Core timer loop executing every second.
    /// Assigns target programs if waiting for a slot, increments time when active, and updates display.
    /// </summary>
    private void Timer_Tick(object sender, object e)
    {
        UpdateIdleState();

        // If user requested to set a program slot, capture the currently active process.
        if (_waitingForSlot != -1)
        {
            string activeProcName = GetActiveProcessName();
            if (!string.IsNullOrEmpty(activeProcName) && activeProcName.ToLower() != "utimer")
            {
                _targetPrograms[_waitingForSlot] = activeProcName;
                UpdateMenuUI();
                _waitingForSlot = -1;
            }
        }

        // Increment time if any target program is focused and user is not idle.
        if (IsAnyTargetProgramActive() && !_isTimedOut)
        {
            _elapsedTime = _elapsedTime.Add(TimeSpan.FromSeconds(1));
        }

        UpdateTimerDisplay();
    }

    /// <summary>
    /// Formats elapsed time strings and applies visual opacity cues when paused/timed out.
    /// </summary>
    private void UpdateTimerDisplay()
    {
        if (_elapsedTime.TotalHours >= 1)
        {
            TimerText.Text = _elapsedTime.ToString(@"hh\:mm\:ss");
        }
        else
        {
            TimerText.Text = _elapsedTime.ToString(@"mm\:ss");
        }

        if (_isTimedOut)
        {
            SubText.Text = string.IsNullOrEmpty(_baseSubText) ? "Paused" : $"{_baseSubText} (Paused)";
            TimerText.Opacity = 0.55;
            SubText.Opacity = 0.7;
        }
        else
        {
            SubText.Text = string.IsNullOrEmpty(_baseSubText) ? SubText.Text : _baseSubText;
            TimerText.Opacity = 1.0;
            SubText.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Identifies the process name of the current foreground window via GetForegroundWindow.
    /// </summary>
    private string GetActiveProcessName()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return string.Empty;

            GetWindowThreadProcessId(hwnd, out uint processId);
            Process proc = Process.GetProcessById((int)processId);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Checks if the active process name matches any configured target program (case-insensitive).
    /// </summary>
    private bool IsAnyTargetProgramActive()
    {
        string activeProc = GetActiveProcessName();
        if (string.IsNullOrEmpty(activeProc)) return false;

        return _targetPrograms.Any(prog => !string.IsNullOrEmpty(prog) &&
                                           string.Equals(activeProc, prog, StringComparison.OrdinalIgnoreCase));
    }

    // ==========================================
    // Settings & Theme Handlers
    // ==========================================

    private void OpacityNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (!sender.IsLoaded) return;

        if (RootBorder == null) return;
        if (_suppressOpacityValueChanged) return;

        double currentVal = sender.Value;
        double roundedVal = Math.Round(currentVal, 2);

        if (Math.Abs(currentVal - roundedVal) > 0.0)
        {
            _suppressOpacityValueChanged = true;
            try { sender.Value = roundedVal; } catch { }
            _suppressOpacityValueChanged = false;
        }

        if (roundedVal < 0.01) roundedVal = 0.01;
        if (roundedVal > 1.0) roundedVal = 1.0;

        _currentOpacity = roundedVal;
        ApplyTheme();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {

        if (sender is ComboBox comboBox && !comboBox.IsLoaded) return;

        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem selectedItem)
        {
            if (selectedItem.Tag is string themeMode)
            {
                _currentTheme = themeMode;
                ApplyTheme();
            }
        }
    }

    /// <summary>
    /// Dynamically applies dark/light colors and acrylic tints based on current theme and opacity.
    /// </summary>
    private void ApplyTheme()
    {
        if (RootBorder == null) return;

        bool isDark = true;
        if (_currentTheme == "Dark")
        {
            isDark = true;
        }
        else if (_currentTheme == "Light")
        {
            isDark = false;
        }
        else if (_currentTheme == "System")
        {
            // Calculate system brightness to detect dark or light system theme.
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            var foreground = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            isDark = (foreground.R + foreground.G + foreground.B) > 380;
        }

        // ⭐ Convert _currentOpacity to Alpha value(0~255)
        byte alpha = (byte)Math.Round(_currentOpacity * 255.0);

        if (_acrylicController != null)
        {
            if (isDark)
            {
                _acrylicController.TintColor = Windows.UI.Color.FromArgb(255, 20, 20, 20);
                _acrylicController.TintOpacity = 0.4f;
                _acrylicController.LuminosityOpacity = 0.15f;
            }
            else
            {
                _acrylicController.TintColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
                _acrylicController.TintOpacity = 0.5f;
                _acrylicController.LuminosityOpacity = 0.6f;
            }
        }

        if (isDark)
        {
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 50, 52, 50));
            TimerText.Foreground = new SolidColorBrush(Color.FromArgb(255, 117, 130, 105));
            SubText.Foreground = new SolidColorBrush(Color.FromArgb(255, 117, 130, 105));
        }
        else
        {
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 240, 240, 240));
            TimerText.Foreground = new SolidColorBrush(Color.FromArgb(255, 60, 70, 60));
            SubText.Foreground = new SolidColorBrush(Color.FromArgb(255, 90, 100, 90));
        }
    }

    /// <summary>
    /// Applies the selected typography or clears local values to revert to default Windows font.
    /// </summary>
    private void ApplyFontFamily()
    {
        if (string.Equals(_currentFontFamily, "WindowsDefault", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(_currentFontFamily))
        {
            TimerText.ClearValue(TextBlock.FontFamilyProperty);
            SubText.ClearValue(TextBlock.FontFamilyProperty);
        }
        else
        {
            var fontFamily = new FontFamily(_currentFontFamily);
            TimerText.FontFamily = fontFamily;
            SubText.FontFamily = fontFamily;
        }
    }

    private void OnSetFont_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string fontName)
        {
            // ⭐ Save changed FontFamily to _currentFontFamily
            _currentFontFamily = fontName;
            ApplyFontFamily();
        }
    }

    private void OnToggleLock_Click(object sender, RoutedEventArgs e)
    {
        _isLocked = !_isLocked;
        MenuLock.IsChecked = _isLocked;
    }

    private void OnResumeTime_Click(object sender, RoutedEventArgs e)
    {
        _elapsedTime = TimeSpan.Zero;
        UpdateTimerDisplay();
    }

    private void OnSetProgram1_Click(object sender, RoutedEventArgs e) { _waitingForSlot = 0; MenuProg1.Text = "Program 1: (Click target window...)"; }
    private void OnSetProgram2_Click(object sender, RoutedEventArgs e) { _waitingForSlot = 1; MenuProg2.Text = "Program 2: (Click target window...)"; }
    private void OnSetProgram3_Click(object sender, RoutedEventArgs e) { _waitingForSlot = 2; MenuProg3.Text = "Program3: (Click target window...)"; }

    private void UpdateMenuUI()
    {
        MenuProg1.Text = $"Program 1: {(_targetPrograms[0] == "" ? "(Not set)" : _targetPrograms[0])}";
        MenuProg2.Text = $"Program 2: {(_targetPrograms[1] == "" ? "(Not set)" : _targetPrograms[1])}";
        MenuProg3.Text = $"Program 3: {(_targetPrograms[2] == "" ? "(Not set)" : _targetPrograms[2])}";
    }

}

// ==========================================
// System Dispatcher Helper
// ==========================================

/// <summary>
/// Helper class required for WinUI Composition backdrops (like Acrylic) in desktop Win32 apps.
/// Ensures a COM DispatcherQueueController is instantiated on the thread.
/// </summary>
public class WindowsSystemDispatcherQueueHelper
{
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options, [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object dispatcherQueueController);

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    private object _dispatcherQueueController = null;

    public void EnsureWindowsSystemDispatcherQueueController()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;

        if (_dispatcherQueueController == null)
        {
            DispatcherQueueOptions options;
            options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
            options.threadType = 2; // DQTYPE_THREAD_CURRENT
            options.apartmentType = 2; // DQTAT_COM_STA

            CreateDispatcherQueueController(options, ref _dispatcherQueueController);
        }
    }
}

// ==========================================
// INI Configuration Helper
// ==========================================

/// <summary>
/// Utility class wrapping native kernel32 profile functions for lightweight .ini file management.
/// </summary>
public static class IniHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPrivateProfileString(string section, string key, string def, System.Text.StringBuilder retVal, int size, string filePath);

    /// <summary>
    /// Resolves path to %LocalAppData%/uTimer/settings.ini, creating the directory if needed.
    /// </summary>
    public static string GetIniPath()
    {
        string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uTimer");
        Directory.CreateDirectory(folderPath);
        return Path.Combine(folderPath, "settings.ini");
    }

    public static void Write(string section, string key, string value)
    {
        WritePrivateProfileString(section, key, value, GetIniPath());
    }

    public static string Read(string section, string key, string defaultValue = "")
    {
        var sb = new System.Text.StringBuilder(255);
        GetPrivateProfileString(section, key, defaultValue, sb, 255, GetIniPath());
        return sb.ToString();
    }
    public static int ReadInt(string section, string key, int defaultValue = 0)
    {
        string str = Read(section, key, "");
        return int.TryParse(str, out int result) ? result : defaultValue;
    }

    public static double ReadDouble(string section, string key, double defaultValue = 0.0)
    {
        string str = Read(section, key, "");
        return double.TryParse(str, out double result) ? result : defaultValue;
    }

    public static bool ReadBool(string section, string key, bool defaultValue = false)
    {
        string str = Read(section, key, "");
        return bool.TryParse(str, out bool result) ? result : defaultValue;
    }
}
