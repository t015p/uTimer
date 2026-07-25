using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.UI;

namespace uTimer;

public sealed partial class MainWindow : Window
{
    private AppWindow _appWindow;
    private DispatcherTimer _timer;
    private TimeSpan _elapsedTime;

    // 3개의 프로그램 슬롯
    private string[] _targetPrograms = new string[3] { "", "", "" };
    private int _waitingForSlot = -1;

    // 설정 상태 변수들
    private string _currentTheme = "Dark";
    private bool _isLocked = false;
    private double _opacityValue = 0.65;

    // Drag handling (left-click should be ignored unless moved beyond system drag threshold)
    private IntPtr _hWnd;
    private bool _leftPressed = false;
    private bool _isDragging = false;
    private POINT _pressCursor;
    private RECT _pressWindowRect;
    private int _dragThresholdX;
    private int _dragThresholdY;
    private bool _suppressOpacityValueChanged = false;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private const int GWL_STYLE = -16;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;

    public MainWindow()
    {
        this.InitializeComponent();

        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _appWindow.Resize(new Windows.Graphics.SizeInt32(200, 200));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;

            try
            {
                IntPtr style = GetWindowLongPtr(hWnd, GWL_STYLE);
                style = (IntPtr)(style.ToInt64() & ~(WS_BORDER | WS_DLGFRAME | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU));
                SetWindowLongPtr(hWnd, GWL_STYLE, style);
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            }
            catch
            {
                // ignore if window style update fails on this OS/build
            }
        }

        this.SystemBackdrop = new DesktopAcrylicBackdrop();

        _hWnd = hWnd;
        _dragThresholdX = GetSystemMetrics(SM_CXDRAG);
        _dragThresholdY = GetSystemMetrics(SM_CYDRAG);

        LoadSettings();
        ApplyTheme();
        UpdateMenuUI();
        ApplyLockStateToUI();

        _elapsedTime = TimeSpan.Zero;

        try { _baseSubText = SubText?.Text ?? string.Empty; } catch { _baseSubText = string.Empty; }

        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Timer_Tick;
        _timer.Start();

        Closed += (_, _) => SaveSettings();
    }

    // --- Win32 메시지 서브클래싱 (클릭은 보존하고, 드래그만 완벽하게 캐치) ---
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

    [DllImport("user32", SetLastError = true)]
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

    private int _timeoutSeconds = 3; // default
    private bool _isTimedOut = false;
    private string _baseSubText = "";

    private void SubclassWindow(IntPtr hWnd)
    {
        _wndProcDelegate = new WndProcDelegate(CustomWndProc);
        IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _oldWndProc = SetWindowLongPtr(hWnd, GWLP_WNDPROC, ptr);
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST)
        {
            if (!_isLocked)
            {
                IntPtr result = CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
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

    private void LoadSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;

        if (settings.Values.TryGetValue("Theme", out object? themeValue) && themeValue is string savedTheme)
        {
            _currentTheme = savedTheme;
        }

        if (settings.Values.TryGetValue("Opacity", out object? opacityValue) && opacityValue is string opacityText && double.TryParse(opacityText, out double parsedOpacity))
        {
            _opacityValue = Math.Clamp(parsedOpacity, 0.01, 1.0);
        }
        else if (settings.Values.TryGetValue("Opacity", out object? opacityValueDouble) && opacityValueDouble is double savedOpacity)
        {
            _opacityValue = Math.Clamp(savedOpacity, 0.01, 1.0);
        }

        if (settings.Values.TryGetValue("Timeout", out object? timeoutValue) && timeoutValue is double timeoutDouble)
        {
            _timeoutSeconds = (int)Math.Clamp(timeoutDouble, 0, 3600);
        }
        else if (settings.Values.TryGetValue("Timeout", out object? timeoutValueInt) && timeoutValueInt is int timeoutInt)
        {
            _timeoutSeconds = (int)Math.Clamp(timeoutInt, 0, 3600);
        }

        if (settings.Values.TryGetValue("Program1", out object? program1Value) && program1Value is string program1)
        {
            _targetPrograms[0] = program1;
        }

        if (settings.Values.TryGetValue("Program2", out object? program2Value) && program2Value is string program2)
        {
            _targetPrograms[1] = program2;
        }

        if (settings.Values.TryGetValue("Program3", out object? program3Value) && program3Value is string program3)
        {
            _targetPrograms[2] = program3;
        }

        if (settings.Values.TryGetValue("LockPosition", out object? lockValue) && lockValue is bool savedLock)
        {
            _isLocked = savedLock;
        }

        if (settings.Values.TryGetValue("FontFamily", out object? fontValue) && fontValue is string savedFont)
        {
            ApplyFontFamily(savedFont);
        }
        else
        {
            ApplyFontFamily("WindowsDefault");
        }

        if (ThemeComboBox != null)
        {
            foreach (var item in ThemeComboBox.Items)
            {
                if (item is ComboBoxItem comboItem && comboItem.Tag is string themeTag && string.Equals(themeTag, _currentTheme, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeComboBox.SelectedItem = comboItem;
                    break;
                }
            }
        }

        if (OpacityNumberBox != null)
        {
            _suppressOpacityValueChanged = true;
            OpacityNumberBox.Value = _opacityValue;
            _suppressOpacityValueChanged = false;
        }

        if (TimeoutNumberBox != null)
        {
            TimeoutNumberBox.Value = _timeoutSeconds;
        }
    }

    private void SaveSettings()
    {
        var settings = ApplicationData.Current.LocalSettings;
        settings.Values["Theme"] = _currentTheme;
        settings.Values["Opacity"] = _opacityValue;
        settings.Values["Timeout"] = _timeoutSeconds;
        settings.Values["Program1"] = _targetPrograms[0];
        settings.Values["Program2"] = _targetPrograms[1];
        settings.Values["Program3"] = _targetPrograms[2];
        settings.Values["LockPosition"] = _isLocked;
        settings.Values["FontFamily"] = GetCurrentFontFamilyName();
    }

    private void ApplyLockStateToUI()
    {
        if (MenuLock != null)
        {
            MenuLock.IsChecked = _isLocked;
        }
    }

    private string GetCurrentFontFamilyName()
    {
        if (TimerText != null && TimerText.FontFamily != null)
        {
            return TimerText.FontFamily.Source;
        }

        return "WindowsDefault";
    }

    private void ApplyFontFamily(string fontName)
    {
        if (TimerText == null || SubText == null)
        {
            return;
        }

        if (string.Equals(fontName, "WindowsDefault", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(fontName))
        {
            TimerText.ClearValue(TextBlock.FontFamilyProperty);
            SubText.ClearValue(TextBlock.FontFamilyProperty);
        }
        else
        {
            var fontFamily = new FontFamily(fontName);
            TimerText.FontFamily = fontFamily;
            SubText.FontFamily = fontFamily;
        }
    }

    private void OnDragAreaPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(null);
        if (pt.Properties.IsRightButtonPressed)
        {
            // let RightTapped handler show the context menu; don't start left-drag logic
            return;
        }

        if (_isLocked)
        {
            _leftPressed = false;
            _isDragging = false;
            e.Handled = true;
            return;
        }

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
        if (_isLocked || !_leftPressed) return;

        GetCursorPos(out POINT current);
        int dx = Math.Abs(current.X - _pressCursor.X);
        int dy = Math.Abs(current.Y - _pressCursor.Y);

        if (!_isDragging)
        {
            if (dx >= _dragThresholdX || dy >= _dragThresholdY)
            {
                _isDragging = true;
            }
            else
            {
                return;
            }
        }

        int newX = _pressWindowRect.Left + (current.X - _pressCursor.X);
        int newY = _pressWindowRect.Top + (current.Y - _pressCursor.Y);
        SetWindowPos(_hWnd, IntPtr.Zero, newX, newY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
        e.Handled = true;
    }

    private void OnDragAreaPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isLocked)
        {
            _leftPressed = false;
            _isDragging = false;
            return;
        }

        if (_leftPressed)
        {
            _leftPressed = false;
            _isDragging = false;
            if (sender is UIElement ui) ui.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnDragAreaRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Show the existing context flyout on the RootBorder
        if (RootBorder != null && RootBorder.ContextFlyout is MenuFlyout flyout)
        {
            flyout.ShowAt(RootBorder, e.GetPosition(RootBorder));
            e.Handled = true;
        }
    }

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
        // enforce integer seconds
        double val = sender.Value;
        int intVal = (int)Math.Round(val);
        if (intVal < 0) intVal = 0;
        if (intVal > 3600) intVal = 3600;

        // update UI value if needed (avoid recursion)
        if (Math.Abs(sender.Value - intVal) > 0.0)
        {
            try { sender.Value = intVal; } catch { }
        }

        _timeoutSeconds = intVal;
        SaveSettings();
    }

    private void Timer_Tick(object sender, object e)
    {
        // update idle/timed-out state every tick
        UpdateIdleState();

        if (_waitingForSlot != -1)
        {
            string activeProcName = GetActiveProcessName();
            if (!string.IsNullOrEmpty(activeProcName) && activeProcName.ToLower() != "utimer")
            {
                _targetPrograms[_waitingForSlot] = activeProcName;
                UpdateMenuUI();
                SaveSettings();
                _waitingForSlot = -1;
            }
        }

        // Only increment when the target program is active and the user is not timed out
        if (IsAnyTargetProgramActive() && !_isTimedOut)
        {
            _elapsedTime = _elapsedTime.Add(TimeSpan.FromSeconds(1));
        }

        UpdateTimerDisplay();
    }

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

        // show paused state when timed out (idle)
        if (_isTimedOut)
        {
            // append paused marker to subtext
            SubText.Text = string.IsNullOrEmpty(_baseSubText) ? "Paused" : $"{_baseSubText} (Paused)";
            // dim main timer and subtext
            TimerText.Opacity = 0.55;
            SubText.Opacity = 0.7;
        }
        else
        {
            // restore original
            SubText.Text = string.IsNullOrEmpty(_baseSubText) ? SubText.Text : _baseSubText;
            TimerText.Opacity = 1.0;
            SubText.Opacity = 1.0;
        }
    }

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

    private bool IsAnyTargetProgramActive()
    {
        bool hasAnyTarget = false;
        foreach (var prog in _targetPrograms)
        {
            if (!string.IsNullOrEmpty(prog)) { hasAnyTarget = true; break; }
        }
        if (!hasAnyTarget) return false;

        string activeProc = GetActiveProcessName();
        if (string.IsNullOrEmpty(activeProc)) return false;

        foreach (var prog in _targetPrograms)
        {
            if (!string.IsNullOrEmpty(prog) && string.Equals(activeProc, prog, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // --- 설정 이벤트 및 기능들 ---

    private void OpacityNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (RootBorder == null) return;
        if (_suppressOpacityValueChanged) return;

        // Round the stored value to 2 decimal places and use rounded value for calculations
        double currentVal = sender.Value;
        double roundedVal = Math.Round(currentVal, 2);

        // enforce displayed value to be exactly rounded
        if (Math.Abs(currentVal - roundedVal) > 0.0)
        {
            _suppressOpacityValueChanged = true;
            try { sender.Value = roundedVal; } catch { }
            _suppressOpacityValueChanged = false;
        }

        if (roundedVal < 0.01) roundedVal = 0.01;
        if (roundedVal > 1.0) roundedVal = 1.0;

        _opacityValue = roundedVal;
        ApplyTheme();
        SaveSettings();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            if (selectedItem.Tag is string themeMode)
            {
                _currentTheme = themeMode;
                ApplyTheme();
                SaveSettings();
            }
        }
    }

    private void ApplyTheme()
    {
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
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            var foreground = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            isDark = (foreground.R + foreground.G + foreground.B) > 380;
        }

        int alpha = (int)Math.Round(_opacityValue * 255.0);
        alpha = Math.Clamp(alpha, 1, 255);

        if (isDark)
        {
            RootBorder.Background = new SolidColorBrush(Color.FromArgb((byte)alpha, 50, 52, 50));
            TimerText.Foreground = new SolidColorBrush(Color.FromArgb(255, 117, 130, 105));
            SubText.Foreground = new SolidColorBrush(Color.FromArgb(255, 117, 130, 105));
        }
        else
        {
            RootBorder.Background = new SolidColorBrush(Color.FromArgb((byte)alpha, 240, 240, 240));
            TimerText.Foreground = new SolidColorBrush(Color.FromArgb(255, 60, 70, 60));
            SubText.Foreground = new SolidColorBrush(Color.FromArgb(255, 90, 100, 90));
        }
    }

    private void OnSetFont_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string fontName)
        {
            ApplyFontFamily(fontName);
            SaveSettings();
        }
    }

    private void OnToggleLock_Click(object sender, RoutedEventArgs e)
    {
        _isLocked = !_isLocked;
        _leftPressed = false;
        _isDragging = false;
        ApplyLockStateToUI();
        SaveSettings();
    }

    private void OnResumeTime_Click(object sender, RoutedEventArgs e)
    {
        _elapsedTime = TimeSpan.Zero;
        UpdateTimerDisplay();
    }

    private void OnSetProgram1_Click(object sender, RoutedEventArgs e) { _waitingForSlot = 0; MenuProg1.Text = "Program 1: (Click target window...)"; SaveSettings(); }
    private void OnSetProgram2_Click(object sender, RoutedEventArgs e) { _waitingForSlot = 1; MenuProg2.Text = "Program 2: (Click target window...)"; SaveSettings(); }
    private void OnSetProgram3_Click(object sender, RoutedEventArgs e) { _waitingForSlot = 2; MenuProg3.Text = "MenuProg3: (Click target window...)"; SaveSettings(); }

    private void UpdateMenuUI()
    {
        MenuProg1.Text = string.IsNullOrEmpty(_targetPrograms[0]) ? "Program 1: (Not set)" : $"Program 1: {_targetPrograms[0]}";
        MenuProg2.Text = string.IsNullOrEmpty(_targetPrograms[1]) ? "Program 2: (Not set)" : $"Program 2: {_targetPrograms[1]}";
        MenuProg3.Text = string.IsNullOrEmpty(_targetPrograms[2]) ? "Program 3: (Not set)" : $"Program 3: {_targetPrograms[2]}";
    }
}