using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OnTimeShutdown.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace OnTimeShutdown;

public partial class MainWindow : Window
{
    private readonly ShutdownService _shutdownService = new();
    private readonly StateStore _store = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _toastTimer;

    private ScheduledShutdownState? _activePlan;
    private int _totalSeconds;
    private int _hour;
    private int _minute;
    private bool _tomorrow;
    private int _durationMinutes;
    private string _mode = "time";
    private bool _initializing = true;
    private bool _isBusy;
    private bool _allowClose;
    private bool _minuteWarningShown;
    private bool _hasShownTrayHint;
    private DateTime _lastDate = DateTime.Today;
    private DateTime _lastPreviewRefresh = DateTime.MinValue;
    private string? _launchNotice;

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayDrawingIcon;
    private Forms.ToolStripMenuItem? _trayCancelItem;

    public MainWindow()
    {
        InitializeComponent();

        UserPreferences preferences = _store.LoadPreferences();
        _mode = preferences.LastMode == "countdown" ? "countdown" : "time";
        _hour = preferences.Hour is >= 0 and <= 23 ? preferences.Hour : (DateTime.Now.Hour + 1) % 24;
        _minute = preferences.Minute is >= 0 and <= 59 ? preferences.Minute : 0;
        _tomorrow = preferences.Tomorrow;
        _durationMinutes = Math.Clamp(
            preferences.DurationMinutes <= 0 ? 30 : preferences.DurationMinutes,
            1,
            99 * 60 + 59);

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _clockTimer.Tick += ClockTimer_Tick;

        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.4)
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };

        LoadExistingPlan();
        RefreshDateChipLabels();
        UpdateInputValues();
        SelectModeTab();

        _initializing = true;
        TodayChip.IsChecked = !_tomorrow;
        TomorrowChip.IsChecked = _tomorrow;
        _initializing = false;
        UpdateIdlePreview();
        SetIdlePresentation(animate: false);
        _clockTimer.Start();
    }

    public void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();

        Topmost = true;
        Topmost = false;
        Focus();

        HideTrayIcon();
    }

    public void PrepareForSystemShutdown()
    {
        _allowClose = true;
        _clockTimer.Stop();
        DisposeTrayIcon();
    }

    private void LoadExistingPlan()
    {
        ScheduledShutdownState? state = _store.LoadState();
        if (state is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        if (!BootClock.IsFromCurrentBoot(state.BootMarkerTicks))
        {
            _store.ClearState();
            _launchNotice = "电脑重启过，上次的关机计划已失效。";
            return;
        }

        if (state.DueUtc <= now.AddSeconds(1))
        {
            _store.ClearState();
            _launchNotice = "上次的关机计划已结束。";
            return;
        }

        if (state.TotalSeconds <= 0)
        {
            state.TotalSeconds = Math.Max(
                1,
                (int)Math.Ceiling((state.DueUtc - state.ScheduledAtUtc).TotalSeconds));
        }

        _activePlan = state;
        _totalSeconds = state.TotalSeconds;
        _minuteWarningShown = false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        OuterGrid.Opacity = 1;
        OuterGrid.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        OuterSlide.Y = 0;
        OuterSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        if (_activePlan is not null)
        {
            SetActivePresentation(animate: true);
        }
        else
        {
            SetIdlePresentation(animate: false);
        }

        if (!string.IsNullOrWhiteSpace(_launchNotice))
        {
            ShowToast(_launchNotice, ToastKind.Info);
            _launchNotice = null;
        }
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        if (_activePlan is not null)
        {
            UpdateActiveCountdown();
            return;
        }

        if (DateTime.Today != _lastDate)
        {
            _lastDate = DateTime.Today;
            RefreshDateChipLabels();
            UpdateIdlePreview();
            return;
        }

        if (_mode == "countdown" &&
            (DateTime.Now - _lastPreviewRefresh).TotalSeconds >= 10)
        {
            UpdateIdlePreview();
        }
    }

    private void ModeTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        bool isTimeMode = TimeTab.IsChecked == true;
        _mode = isTimeMode ? "time" : "countdown";
        TimePanel.Visibility = isTimeMode ? Visibility.Visible : Visibility.Collapsed;
        CountdownPanel.Visibility = isTimeMode ? Visibility.Collapsed : Visibility.Visible;

        UpdateIdlePreview();
        UpdateActionPresentation();
        FadeIn(isTimeMode ? TimePanel : CountdownPanel);
    }

    private void SelectModeTab()
    {
        _initializing = true;
        TimeTab.IsChecked = _mode == "time";
        CountdownTab.IsChecked = _mode == "countdown";
        TimePanel.Visibility = _mode == "time" ? Visibility.Visible : Visibility.Collapsed;
        CountdownPanel.Visibility = _mode == "countdown" ? Visibility.Visible : Visibility.Collapsed;
        _initializing = false;
    }

    private void DateChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { IsChecked: true } chip)
        {
            return;
        }

        _tomorrow = string.Equals(chip.Tag as string, "tomorrow", StringComparison.Ordinal);
        UpdateTimeTargetPreview();
    }

    private void TimePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        string[] parts = tag.Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int hour) ||
            !int.TryParse(parts[1], out int minute))
        {
            return;
        }

        _hour = Math.Clamp(hour, 0, 23);
        _minute = Math.Clamp(minute, 0, 59);
        UpdateInputValues();
        UpdateTimeTargetPreview();
    }

    private void DurationPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
        {
            return;
        }

        _durationMinutes = Math.Clamp(minutes, 1, 99 * 60 + 59);
        UpdateInputValues();
        UpdateCountdownTargetPreview();
    }

    private void HourUp_Click(object sender, RoutedEventArgs e)
    {
        _hour = (_hour + 1) % 24;
        UpdateInputValues();
        UpdateTimeTargetPreview();
    }

    private void HourDown_Click(object sender, RoutedEventArgs e)
    {
        _hour = (_hour + 23) % 24;
        UpdateInputValues();
        UpdateTimeTargetPreview();
    }

    private void MinuteUp_Click(object sender, RoutedEventArgs e)
    {
        _minute = (_minute + 1) % 60;
        UpdateInputValues();
        UpdateTimeTargetPreview();
    }

    private void MinuteDown_Click(object sender, RoutedEventArgs e)
    {
        _minute = (_minute + 59) % 60;
        UpdateInputValues();
        UpdateTimeTargetPreview();
    }

    private void HourField_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            HourUp_Click(sender, e);
        }
        else
        {
            HourDown_Click(sender, e);
        }

        e.Handled = true;
    }

    private void MinuteField_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            MinuteUp_Click(sender, e);
        }
        else
        {
            MinuteDown_Click(sender, e);
        }

        e.Handled = true;
    }

    private void DurationHourUp_Click(object sender, RoutedEventArgs e)
    {
        int hours = _durationMinutes / 60;
        if (hours < 99)
        {
            _durationMinutes = Math.Min(_durationMinutes + 60, 99 * 60 + 59);
        }

        UpdateInputValues();
        UpdateCountdownTargetPreview();
    }

    private void DurationHourDown_Click(object sender, RoutedEventArgs e)
    {
        int hours = _durationMinutes / 60;
        if (hours > 0)
        {
            _durationMinutes = Math.Max(_durationMinutes - 60, 1);
        }

        UpdateInputValues();
        UpdateCountdownTargetPreview();
    }

    private void DurationMinuteUp_Click(object sender, RoutedEventArgs e)
    {
        _durationMinutes = Math.Min(_durationMinutes + 1, 99 * 60 + 59);
        UpdateInputValues();
        UpdateCountdownTargetPreview();
    }

    private void DurationMinuteDown_Click(object sender, RoutedEventArgs e)
    {
        _durationMinutes = Math.Max(_durationMinutes - 1, 1);
        UpdateInputValues();
        UpdateCountdownTargetPreview();
    }

    private void ValueInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (textBox.IsKeyboardFocusWithin)
                    {
                        textBox.SelectAll();
                    }
                }),
                DispatcherPriority.Input);
        }
    }

    private void ValueInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox textBox && !textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void ValueInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private void ValueInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            CommitValueInput(textBox);
        }
    }

    private void ValueInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitValueInput(textBox);
            ActionButton.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateInputValues();
            ActionButton.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            AdjustValueInput(textBox, 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            AdjustValueInput(textBox, -1);
            e.Handled = true;
        }
    }

    private void CommitValueInput(TextBox textBox)
    {
        if (!int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            UpdateInputValues();
            return;
        }

        switch (textBox.Name)
        {
            case nameof(HourValueText):
                _hour = Math.Clamp(value, 0, 23);
                UpdateInputValues();
                UpdateTimeTargetPreview();
                break;

            case nameof(MinuteValueText):
                _minute = Math.Clamp(value, 0, 59);
                UpdateInputValues();
                UpdateTimeTargetPreview();
                break;

            case nameof(DurationHourValueText):
            {
                int minutes = _durationMinutes % 60;
                _durationMinutes = Math.Clamp(value, 0, 99) * 60 + minutes;
                if (_durationMinutes <= 0)
                {
                    _durationMinutes = 1;
                }

                UpdateInputValues();
                UpdateCountdownTargetPreview();
                break;
            }

            case nameof(DurationMinuteValueText):
            {
                int hours = _durationMinutes / 60;
                _durationMinutes = hours * 60 + Math.Clamp(value, 0, 59);
                if (_durationMinutes <= 0)
                {
                    _durationMinutes = 1;
                }

                UpdateInputValues();
                UpdateCountdownTargetPreview();
                break;
            }
        }
    }

    private void AdjustValueInput(TextBox textBox, int direction)
    {
        switch (textBox.Name)
        {
            case nameof(HourValueText):
                if (direction > 0)
                {
                    HourUp_Click(textBox, new RoutedEventArgs());
                }
                else
                {
                    HourDown_Click(textBox, new RoutedEventArgs());
                }

                break;

            case nameof(MinuteValueText):
                if (direction > 0)
                {
                    MinuteUp_Click(textBox, new RoutedEventArgs());
                }
                else
                {
                    MinuteDown_Click(textBox, new RoutedEventArgs());
                }

                break;

            case nameof(DurationHourValueText):
                if (direction > 0)
                {
                    DurationHourUp_Click(textBox, new RoutedEventArgs());
                }
                else
                {
                    DurationHourDown_Click(textBox, new RoutedEventArgs());
                }

                break;

            case nameof(DurationMinuteValueText):
                if (direction > 0)
                {
                    DurationMinuteUp_Click(textBox, new RoutedEventArgs());
                }
                else
                {
                    DurationMinuteDown_Click(textBox, new RoutedEventArgs());
                }

                break;
        }

        textBox.SelectAll();
    }

    private void DurationHourField_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            DurationHourUp_Click(sender, e);
        }
        else
        {
            DurationHourDown_Click(sender, e);
        }

        e.Handled = true;
    }

    private void DurationMinuteField_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            DurationMinuteUp_Click(sender, e);
        }
        else
        {
            DurationMinuteDown_Click(sender, e);
        }

        e.Handled = true;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            if (_activePlan is not null)
            {
                await CancelActivePlanAsync(showWindowFirst: false);
            }
            else
            {
                await StartPlanAsync();
            }
        }
        catch (Exception ex)
        {
            _isBusy = false;
            ActionButton.IsEnabled = true;
            SetupArea.IsEnabled = true;
            UpdateActionPresentation();
            ShowToast($"操作未完成：{ex.Message}", ToastKind.Error);
        }
    }

    private async Task StartPlanAsync()
    {
        CommitPendingValueInputs();

        bool rolledToTomorrow = false;
        DateTimeOffset due;
        int seconds;

        if (_mode == "time")
        {
            due = BuildAbsoluteDue(out rolledToTomorrow);
            if (rolledToTomorrow && !_tomorrow)
            {
                _tomorrow = true;
                _initializing = true;
                TomorrowChip.IsChecked = true;
                _initializing = false;
                UpdateTimeTargetPreview();
            }

            seconds = Math.Max(1, (int)Math.Ceiling((due - DateTimeOffset.Now).TotalSeconds));
        }
        else
        {
            due = DateTimeOffset.Now.AddMinutes(_durationMinutes);
            seconds = _durationMinutes * 60;
        }

        _isBusy = true;
        ActionButton.IsEnabled = false;
        SetupArea.IsEnabled = false;
        ActionGlyph.Text = "\uE823";
        ActionText.Text = "正在安排…";

        string reason = $"准点关机：计划在 {due.ToLocalTime():HH:mm} 自动关机";
        ShutdownCommandResult result = await Task.Run(() => _shutdownService.Schedule(seconds, reason));

        if (!result.Success)
        {
            _isBusy = false;
            ActionButton.IsEnabled = true;
            SetupArea.IsEnabled = true;
            UpdateActionPresentation();
            ShowToast(result.Message, ToastKind.Error);
            return;
        }

        var state = new ScheduledShutdownState
        {
            ScheduledAtUtc = DateTimeOffset.Now,
            DueUtc = due,
            TotalSeconds = seconds,
            Mode = _mode,
            TargetLabel = FormatFullDate(due.LocalDateTime),
            BootMarkerTicks = BootClock.CurrentMarker()
        };

        _activePlan = state;
        _totalSeconds = seconds;
        _minuteWarningShown = false;
        _isBusy = false;
        ActionButton.IsEnabled = true;
        SetupArea.IsEnabled = true;

        try
        {
            _store.SaveState(state);
        }
        catch
        {
            ShowToast("关机已安排，但计划状态保存失败，请保持程序运行。", ToastKind.Error);
        }

        SavePreferences();
        SetActivePresentation(animate: true);

        if (rolledToTomorrow)
        {
            ShowToast("今天的时间已过，已自动安排到明天。", ToastKind.Info);
        }
        else
        {
            ShowToast($"已安排，Windows 将在 {due.ToLocalTime():HH:mm} 关机。", ToastKind.Success);
        }
    }

    private async Task CancelActivePlanAsync(bool showWindowFirst)
    {
        if (_activePlan is null || _isBusy)
        {
            return;
        }

        if (showWindowFirst)
        {
            RestoreFromTray();
        }

        _isBusy = true;
        ActionButton.IsEnabled = false;
        ActionGlyph.Text = "\uE711";
        ActionText.Text = "正在取消…";

        ShutdownCommandResult result = await Task.Run(() => _shutdownService.Cancel());

        bool noPlanInSystem = result.ExitCode == 1116;
        if (result.Success || noPlanInSystem)
        {
            _isBusy = false;
            _activePlan = null;
            _store.ClearState();

            ActionButton.IsEnabled = true;
            SetIdlePresentation(animate: true);
            UpdateActionPresentation();
            UpdateIdlePreview();

            if (result.Success)
            {
                ShowToast("已取消关机计划。", ToastKind.Success);
            }
            else
            {
                ShowToast("系统里没有等待执行的计划，界面已重置。", ToastKind.Info);
            }

            return;
        }

        _isBusy = false;
        ActionButton.IsEnabled = true;
        UpdateActionPresentation();
        ShowToast(result.Message, ToastKind.Error);
    }

    private DateTimeOffset BuildAbsoluteDue(out bool rolledToTomorrow)
    {
        DateTime date = DateTime.Today.AddDays(_tomorrow ? 1 : 0);
        var localDateTime = new DateTime(date.Year, date.Month, date.Day, _hour, _minute, 0, DateTimeKind.Unspecified);
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(localDateTime);
        var due = new DateTimeOffset(localDateTime, offset);

        rolledToTomorrow = false;
        if (due <= DateTimeOffset.Now.AddSeconds(5))
        {
            due = due.AddDays(1);
            rolledToTomorrow = true;
        }

        return due;
    }

    private void CommitPendingValueInputs()
    {
        CommitValueInput(HourValueText);
        CommitValueInput(MinuteValueText);
        CommitValueInput(DurationHourValueText);
        CommitValueInput(DurationMinuteValueText);
    }

    private void UpdateInputValues()
    {
        HourValueText.Text = _hour.ToString("00", CultureInfo.InvariantCulture);
        MinuteValueText.Text = _minute.ToString("00", CultureInfo.InvariantCulture);

        int hours = _durationMinutes / 60;
        int minutes = _durationMinutes % 60;
        DurationHourValueText.Text = hours.ToString("00", CultureInfo.InvariantCulture);
        DurationMinuteValueText.Text = minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private void UpdateIdlePreview()
    {
        if (_mode == "time")
        {
            UpdateTimeTargetPreview();
        }
        else
        {
            UpdateCountdownTargetPreview();
        }

        _lastPreviewRefresh = DateTime.Now;
    }

    private void UpdateTimeTargetPreview()
    {
        DateTimeOffset due = BuildAbsoluteDue(out bool rolled);
        string time = due.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

        if (rolled)
        {
            TimeTargetText.Text = $"今天时间已过，将安排到明天 {time}";
            TimeTargetText.Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0x6A, 0x37));
        }
        else
        {
            string relative = FormatRelativeDate(due.LocalDateTime);
            TimeTargetText.Text = $"将在 {relative} {time} 关机";
            TimeTargetText.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x72, 0x68));
        }

        TimeTargetText.ToolTip = $"{FormatFullDate(due.LocalDateTime)} {time}";
    }

    private void UpdateCountdownTargetPreview()
    {
        DateTimeOffset due = DateTimeOffset.Now.AddMinutes(_durationMinutes);
        string time = due.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        string relative = FormatRelativeDate(due.LocalDateTime);
        CountdownTargetText.Text = $"预计 {relative} {time} 关机";
        CountdownTargetText.ToolTip = $"{FormatFullDate(due.LocalDateTime)} {time}";
        _lastPreviewRefresh = DateTime.Now;
    }

    private void RefreshDateChipLabels()
    {
        DateTime today = DateTime.Today;
        DateTime tomorrow = today.AddDays(1);
        TodayChip.ToolTip = $"今天 · {FormatDateWithWeekday(today)}";
        TomorrowChip.ToolTip = $"明天 · {FormatDateWithWeekday(tomorrow)}";
        _lastDate = today;
    }

    private void SetIdlePresentation(bool animate)
    {
        ActiveArea.Visibility = Visibility.Collapsed;
        SetupArea.Visibility = Visibility.Visible;
        SetupArea.IsEnabled = true;

        HeroStatusText.Text = "等待设定";
        HeroStatusDot.Fill = BrushFrom("#F0B37F");
        CountdownText.Text = "待设置";
        CountdownText.FontFamily = (FontFamily)FindResource("UiFont");
        CountdownText.FontSize = 34;
        CountdownText.FontWeight = FontWeights.SemiBold;
        HeroHintText.Text = "选择一个时间，让电脑到点休息";
        RingCaption.Text = "待机";
        CountdownRing.Progress = 0;
        CountdownRing.RingBrush = BrushFrom("#F4A76E");
        CountdownRing.TrackBrush = BrushFrom("#24FFFFFF");
        ActiveRemainingText.Text = "";
        FooterText.Text = "系统级定时 · 关闭窗口即退出";
        UpdateTrayTooltip();

        UpdateActionPresentation();

        if (animate)
        {
            FadeIn(SetupArea);
        }
    }

    private void SetActivePresentation(bool animate)
    {
        if (_activePlan is null)
        {
            return;
        }

        SetupArea.Visibility = Visibility.Collapsed;
        ActiveArea.Visibility = Visibility.Visible;

        HeroStatusText.Text = "关机计划已启动";
        HeroStatusDot.Fill = BrushFrom("#7FE3B6");
        CountdownText.Text = "00:00:00";
        CountdownText.FontFamily = (FontFamily)FindResource("NumberFont");
        CountdownText.FontSize = 34;
        CountdownText.FontWeight = FontWeights.SemiBold;
        HeroHintText.Text = "Windows 会准时关闭电脑";
        RingCaption.Text = "进行中";
        CountdownRing.RingBrush = BrushFrom("#F4A76E");
        CountdownRing.TrackBrush = BrushFrom("#24FFFFFF");
        FooterText.Text = "计划进行中 · 关闭窗口将最小化到托盘";

        ActiveTargetDateText.Text = FormatFullDate(_activePlan.DueUtc.LocalDateTime);
        ActiveTargetTimeText.Text = _activePlan.DueUtc.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        ActiveModeText.Text = _activePlan.Mode == "countdown" ? "倒计时" : "指定时间";

        UpdateActionPresentation();
        UpdateActiveCountdown();
        UpdateTrayTooltip();

        if (animate)
        {
            FadeIn(ActiveArea);
        }
    }

    private void UpdateActiveCountdown()
    {
        if (_activePlan is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan remaining = _activePlan.DueUtc - now;
        int secondsLeft = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));

        string formatted = FormatCountdown(secondsLeft);
        CountdownText.Text = secondsLeft == 0 ? "正在关机" : formatted;
        ActiveRemainingText.Text = formatted;

        double progress = _totalSeconds > 0
            ? Math.Clamp(secondsLeft / (double)_totalSeconds, 0d, 1d)
            : 0d;
        CountdownRing.Progress = progress;

        ActiveTargetDateText.Text = FormatFullDate(_activePlan.DueUtc.LocalDateTime);
        ActiveTargetTimeText.Text = _activePlan.DueUtc.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (secondsLeft == 0)
        {
            HeroStatusText.Text = "正在关机";
            HeroStatusDot.Fill = BrushFrom("#FF927F");
            CountdownText.Text = "正在关机";
            CountdownText.FontFamily = (FontFamily)FindResource("UiFont");
            CountdownText.FontSize = 30;
            CountdownText.FontWeight = FontWeights.SemiBold;
            HeroHintText.Text = "Windows 正在关闭电脑，请保存好文件";
            RingCaption.Text = "即将关机";
            CountdownRing.RingBrush = BrushFrom("#FF927F");
            CountdownRing.Progress = 1;
            return;
        }

        if (secondsLeft <= 60)
        {
            HeroStatusText.Text = "即将关机";
            HeroStatusDot.Fill = BrushFrom("#FF927F");
            CountdownRing.RingBrush = BrushFrom("#FF927F");
            RingCaption.Text = "即将关机";
            HeroHintText.Text = "还有不到 1 分钟，请保存好文件";
            CountdownText.FontFamily = (FontFamily)FindResource("NumberFont");
            CountdownText.FontSize = 34;
            CountdownText.FontWeight = FontWeights.SemiBold;

            if (!_minuteWarningShown)
            {
                _minuteWarningShown = true;
                NotifyMinuteWarning();
            }
        }
        else
        {
            HeroStatusText.Text = "关机计划已启动";
            HeroStatusDot.Fill = BrushFrom("#7FE3B6");
            CountdownRing.RingBrush = BrushFrom("#F4A76E");
            RingCaption.Text = "进行中";
            HeroHintText.Text = "Windows 会准时关闭电脑";
            CountdownText.FontFamily = (FontFamily)FindResource("NumberFont");
            CountdownText.FontSize = 34;
            CountdownText.FontWeight = FontWeights.SemiBold;
        }

        UpdateTrayTooltip();
    }

    private void UpdateActionPresentation()
    {
        if (_activePlan is not null)
        {
            ActionButton.Style = (Style)FindResource("DangerActionStyle");
            ActionGlyph.Text = "\uE711";
            ActionText.Text = "取消关机";
            AutomationProperties.SetName(ActionButton, "取消关机");
        }
        else
        {
            ActionButton.Style = (Style)FindResource("PrimaryActionStyle");
            ActionGlyph.Text = "\uE7E8";
            ActionText.Text = _mode == "time" ? "安排关机" : "开始倒计时";
            AutomationProperties.SetName(ActionButton, ActionText.Text);
        }
    }

    private void NotifyMinuteWarning()
    {
        if (IsVisible)
        {
            ShowToast("还有 1 分钟将关机，请保存好文件。", ToastKind.Warning);
            return;
        }

        EnsureTrayIcon();
        _trayIcon?.ShowBalloonTip(
            5_000,
            "准点关机 · 还有 1 分钟",
            "Windows 即将关闭电脑，请保存好文件。",
            Forms.ToolTipIcon.Warning);
    }

    private void SavePreferences()
    {
        _store.SavePreferences(new UserPreferences
        {
            LastMode = _mode,
            Hour = _hour,
            Minute = _minute,
            Tomorrow = _tomorrow,
            DurationMinutes = _durationMinutes
        });
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 1)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The mouse was released before the window started moving.
            }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isBusy && !_allowClose)
        {
            e.Cancel = true;
            ShowToast("正在处理关机计划，请稍候…", ToastKind.Info);
            return;
        }

        if (_allowClose)
        {
            SavePreferences();
            _clockTimer.Stop();
            DisposeTrayIcon();
            return;
        }

        if (_activePlan is null)
        {
            SavePreferences();
            _clockTimer.Stop();
            DisposeTrayIcon();
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        try
        {
            EnsureTrayIcon();
        }
        catch (Exception ex)
        {
            ShowToast($"无法最小化到托盘：{ex.Message}", ToastKind.Error);
            return;
        }

        Hide();
        ShowInTaskbar = false;

        if (!_hasShownTrayHint)
        {
            _hasShownTrayHint = true;
            _trayIcon?.ShowBalloonTip(
                4_000,
                "关机计划仍在进行",
                "窗口已最小化到托盘，Windows 仍会按时关机。双击托盘图标可重新打开。",
                Forms.ToolTipIcon.Info);
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
            return;
        }

        _trayDrawingIcon = LoadApplicationIcon();
        _trayMenu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Drawing.Font("Microsoft YaHei UI", 9f)
        };

        var openItem = new Forms.ToolStripMenuItem("打开主界面")
        {
            Font = new Drawing.Font("Microsoft YaHei UI", 9f, Drawing.FontStyle.Bold)
        };
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));

        _trayCancelItem = new Forms.ToolStripMenuItem("取消关机");
        _trayCancelItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            await CancelActivePlanAsync(showWindowFirst: true);
        }));

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitFromTray));

        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(_trayCancelItem);
        _trayMenu.Items.Add(exitItem);
        _trayMenu.Opening += (_, _) =>
        {
            _trayCancelItem.Enabled = _activePlan is not null;
        };

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon,
            Text = "准点关机",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
        UpdateTrayTooltip();
    }

    private Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                Drawing.Icon? icon = Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall through to the generic system icon.
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null)
        {
            return;
        }

        if (_activePlan is not null)
        {
            TimeSpan remaining = _activePlan.DueUtc - DateTimeOffset.Now;
            int secondsLeft = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            _trayIcon.Text = $"准点关机 · 距关机 {FormatCountdown(secondsLeft)}";
        }
        else
        {
            _trayIcon.Text = "准点关机";
        }
    }

    private void HideTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayCancelItem = null;
        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
    }

    private void ExitFromTray()
    {
        if (_activePlan is not null)
        {
            MessageBoxResult answer = MessageBox.Show(
                "退出后，已安排的关机计划仍会由 Windows 执行。\n\n" +
                "是：取消关机并退出\n" +
                "否：保留关机计划并退出\n" +
                "取消：返回程序",
                "退出准点关机",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                ShutdownCommandResult result = _shutdownService.Cancel();
                if (!result.Success && result.ExitCode != 1116)
                {
                    MessageBox.Show(
                        $"无法取消关机计划：{result.Message}",
                        "准点关机",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                _store.ClearState();
                _activePlan = null;
            }
        }

        _allowClose = true;
        _clockTimer.Stop();
        DisposeTrayIcon();
        SavePreferences();
        Application.Current.Shutdown();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isBusy)
        {
            ActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        }
    }

    private void ShowToast(string message, ToastKind kind)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        (string glyph, string color) = kind switch
        {
            ToastKind.Success => ("\uE73E", "#8FE0BC"),
            ToastKind.Warning => ("\uE7BA", "#FFC08D"),
            ToastKind.Error => ("\uE7BA", "#FF9B8F"),
            _ => ("\uE946", "#FFC08D")
        };

        ToastText.Text = message;
        ToastIcon.Text = glyph;
        ToastIcon.Foreground = BrushFrom(color);
        Toast.Visibility = Visibility.Visible;
        Toast.Opacity = 0;

        Toast.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        if (Toast.Visibility != Visibility.Visible)
        {
            return;
        }

        var animation = new DoubleAnimation(Toast.Opacity, 0, TimeSpan.FromMilliseconds(180));
        animation.Completed += (_, _) =>
        {
            if (Toast.Opacity < 0.05)
            {
                Toast.Visibility = Visibility.Collapsed;
            }
        };
        Toast.BeginAnimation(OpacityProperty, animation);
    }

    private static void FadeIn(UIElement element)
    {
        element.Opacity = 0;
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private static string FormatCountdown(int totalSeconds)
    {
        totalSeconds = Math.Max(0, totalSeconds);
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds % 3600 / 60;
        int seconds = totalSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    private static string FormatRelativeDate(DateTime dateTime)
    {
        int difference = (dateTime.Date - DateTime.Today).Days;
        return difference switch
        {
            0 => "今天",
            1 => "明天",
            2 => "后天",
            _ => dateTime.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"))
        };
    }

    private static string FormatFullDate(DateTime dateTime)
    {
        int difference = (dateTime.Date - DateTime.Today).Days;
        string prefix = difference switch
        {
            0 => "今天 · ",
            1 => "明天 · ",
            2 => "后天 · ",
            _ => string.Empty
        };

        return prefix + dateTime.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN")) +
               " " + FormatWeekday(dateTime);
    }

    private static string FormatDateWithWeekday(DateTime dateTime)
    {
        return dateTime.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN")) +
               " " + FormatWeekday(dateTime);
    }

    private static string FormatWeekday(DateTime dateTime)
    {
        const string weekdays = "日一二三四五六";
        return "周" + weekdays[(int)dateTime.DayOfWeek];
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _toastTimer.Stop();
        DisposeTrayIcon();
        base.OnClosed(e);
    }

    private enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error
    }
}
