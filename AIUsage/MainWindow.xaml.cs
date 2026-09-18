using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace AIUsage;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Korean = new("ko-KR");
    private static readonly int[] RefreshChoices = [1, 5, 10];
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "AIUsage";
    private const string DisplayName = "AI Usage";
    private const double EstimatedHeight = 110;

    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, App.ShowEventName);
    private RegisteredWaitHandle? _showWait;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _menu;
    private UsageSnapshot? _last;
    private UsageException? _error;
    private bool _fetching;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        Topmost = _settings.Topmost;
        if (Array.IndexOf(RefreshChoices, _settings.RefreshMinutes) < 0)
            _settings.RefreshMinutes = 5;

        PlaceWindow();
        BuildTray();

        // 프로그램을 한 번 더 실행하면 이 창을 다시 보이게 한다
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => Dispatcher.BeginInvoke(ShowGadget), null, Timeout.Infinite, false);

        // CLI 조회는 설정한 간격마다, 남은 시간 문구는 20초마다 다시 계산
        _refreshTimer.Interval = TimeSpan.FromMinutes(_settings.RefreshMinutes);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _tickTimer.Tick += (_, _) => Render();

        Loaded += async (_, _) =>
        {
            _last = UsageProbe.LoadCache();
            Render();
            await RefreshAsync();
        };
        _refreshTimer.Start();
        _tickTimer.Start();
    }

    // ───── 데이터 ─────

    private async Task RefreshAsync()
    {
        if (_fetching || _exiting) return;
        _fetching = true;
        Render();
        try
        {
            _last = await UsageProbe.FetchAsync();
            _error = null;
        }
        catch (UsageException ex)
        {
            // 실패해도 마지막 값은 유지
            _error = ex;
        }
        catch (Exception ex)
        {
            _error = new UsageException("조회 오류", ex.Message);
        }
        finally
        {
            _fetching = false;
        }
        Render();
    }

    private void Render()
    {
        RowsPanel.Children.Clear();

        bool hasData = _last is { Windows.Count: > 0 };
        if (hasData)
        {
            // 게이지가 3개면 조금 작게
            double size = _last!.Windows.Count >= 3 ? 50 : 64;
            RowsPanel.Columns = _last.Windows.Count;
            foreach (var w in _last.Windows)
                RowsPanel.Children.Add(BuildGauge(w.Name, IsExpired(w) ? 0 : w.Percent, ResetText(w), size));
        }
        else
        {
            // CLI가 없으면 조회가 진행되지 않으므로 "멈춤"으로 표시
            string placeholder = _error?.Message == UsageProbe.CliMissing ? "멈춤" : "대기 중";
            RowsPanel.Columns = 2;
            RowsPanel.Children.Add(BuildGauge("세션", null, placeholder, 64));
            RowsPanel.Children.Add(BuildGauge("주간", null, placeholder, 64));
        }

        var tooltip = new List<string>();
        if (hasData)
            tooltip.Add($"{_last!.UpdatedAt.ToLocalTime().ToString("M/d tt h:mm", Korean)} 조회 · {_settings.RefreshMinutes}분마다 갱신");

        if (_fetching)
        {
            StatusText.Text = "조회 중…";
            StatusText.Foreground = MakeBrush("#D0D0D0");
        }
        else if (_error is not null)
        {
            StatusText.Text = _error.Message;
            StatusText.Foreground = MakeBrush("#E8A33D");
            tooltip.Add(_error.Detail ?? _error.Message);
        }
        else if (hasData)
        {
            var updated = _last!.UpdatedAt.ToLocalTime();
            StatusText.Text = updated.Date == DateTime.Today
                ? updated.ToString("tt h:mm", Korean)
                : updated.ToString("M/d H:mm", Korean);
            StatusText.Foreground = MakeBrush("#D0D0D0");
        }
        else
        {
            StatusText.Text = "대기 중";
            StatusText.Foreground = MakeBrush("#D0D0D0");
        }
        Card.ToolTip = tooltip.Count > 0 ? string.Join("\n", tooltip) : "Claude Code CLI로 사용량을 조회합니다";

        UpdateTrayText();
    }

    private static bool IsExpired(UsageWindow w) => w.ResetsAt is DateTimeOffset r && r <= DateTimeOffset.Now;

    /// <summary>원형 게이지: 가운데에 %와 이름, 아래에 재설정 문구. percent가 null이면 빈 게이지.</summary>
    private static UIElement BuildGauge(string name, double? percent, string resetText, double size)
    {
        const double stroke = 6;
        double pct = Math.Clamp(percent ?? 0, 0, 100);

        var ring = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(new Shapes.Ellipse { Stroke = MakeBrush("#33FFFFFF"), StrokeThickness = stroke });
        if (percent is not null && pct > 0)
            ring.Children.Add(BuildArc(pct, size, stroke, BarBrush(pct)));

        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(new TextBlock
        {
            Text = percent is null ? "--" : $"{pct:0}%",
            Foreground = MakeBrush("#F0F0F0"),
            FontSize = size >= 60 ? 14 : 11.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        center.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = MakeBrush("#A0A0A0"),
            FontSize = size >= 60 ? 9.5 : 8.5,
            Margin = new Thickness(0, -2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        ring.Children.Add(center);

        var panel = new StackPanel();
        panel.Children.Add(ring);
        panel.Children.Add(new TextBlock
        {
            Text = resetText,
            Foreground = MakeBrush("#D0D0D0"),
            FontSize = 9.5,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return panel;
    }

    private static UIElement BuildArc(double pct, double size, double stroke, Brush brush)
    {
        double c = size / 2, r = (size - stroke) / 2;
        if (pct >= 99.9)
            return new Shapes.Ellipse { Stroke = brush, StrokeThickness = stroke };

        // 12시 방향에서 시계 방향으로
        double angle = pct / 100 * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsFilled = false };
        figure.Segments.Add(new ArcSegment(
            new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)),
            new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true));

        return new Shapes.Path
        {
            Data = new PathGeometry([figure]),
            Stroke = brush,
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private static string ResetText(UsageWindow w)
    {
        if (w.ResetsAt is not DateTimeOffset resetsAt)
            return "";
        if (IsExpired(w))
            return "재설정됨";

        if (w.Key == "session")
        {
            var left = resetsAt - DateTimeOffset.Now;
            return left.TotalHours >= 1
                ? $"{(int)left.TotalHours}시간 {left.Minutes}분 후"
                : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}분 후";
        }

        return resetsAt.ToLocalTime().ToString("(ddd) tt h:mm", Korean);
    }

    private static Brush BarBrush(double pct) =>
        pct >= 90 ? MakeBrush("#E5534B") : pct >= 70 ? MakeBrush("#E8A33D") : MakeBrush("#4C8DF6");

    private static SolidColorBrush MakeBrush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    // ───── 창 위치 / 이동 ─────

    private void PlaceWindow()
    {
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth, bottom = top + SystemParameters.VirtualScreenHeight;

        if (_settings.Left is double x && _settings.Top is double y &&
            x >= left && x + 40 <= right && y >= top && y + 40 <= bottom)
        {
            Left = x;
            Top = y;
        }
        else
        {
            CenterOnPrimaryScreen();
        }
    }

    /// <summary>처음 실행하거나 위치를 초기화하면 주 모니터 가운데에 둔다.</summary>
    private void CenterOnPrimaryScreen()
    {
        var area = SystemParameters.WorkArea;
        double height = ActualHeight > 0 ? ActualHeight : EstimatedHeight;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - height) / 2;
    }

    private async void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            await RefreshAsync();
            return;
        }
        DragMove();
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private void Card_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _menu?.Show(Forms.Cursor.Position);
    }

    // ───── 트레이 / 메뉴 ─────

    private void BuildTray()
    {
        _menu = new Forms.ContextMenuStrip();

        var refreshItem = new Forms.ToolStripMenuItem("지금 새로고침", null, async (_, _) => await RefreshAsync());
        var intervalItem = new Forms.ToolStripMenuItem("조회 간격");
        foreach (int minutes in RefreshChoices)
        {
            int value = minutes;
            intervalItem.DropDownItems.Add(new Forms.ToolStripMenuItem($"{value}분", null, (_, _) => SetRefreshMinutes(value))
            {
                Tag = value,
            });
        }
        var topmostItem = new Forms.ToolStripMenuItem("항상 위에 표시", null, (_, _) => ToggleTopmost());
        var autoStartItem = new Forms.ToolStripMenuItem("Windows 시작 시 실행", null, (_, _) => AutoStart = !AutoStart);
        var resetPosItem = new Forms.ToolStripMenuItem("화면 가운데로 이동", null, (_, _) => ResetPosition());
        var showItem = new Forms.ToolStripMenuItem("숨기기", null, (_, _) => ToggleVisible());
        var aboutItem = new Forms.ToolStripMenuItem("정보", null, (_, _) => ShowAbout());
        var exitItem = new Forms.ToolStripMenuItem("종료", null, (_, _) => ExitApp());

        _menu.Items.AddRange([refreshItem, intervalItem, new Forms.ToolStripSeparator(),
            topmostItem, autoStartItem, resetPosItem, new Forms.ToolStripSeparator(), showItem, aboutItem, exitItem]);
        _menu.Opening += (_, _) =>
        {
            refreshItem.Enabled = !_fetching;
            foreach (Forms.ToolStripMenuItem item in intervalItem.DropDownItems)
                item.Checked = (int)item.Tag! == _settings.RefreshMinutes;
            topmostItem.Checked = Topmost;
            autoStartItem.Checked = AutoStart;
            showItem.Text = IsVisible ? "숨기기" : "보이기";
        };

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = DisplayName,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleVisible();
        };
    }

    private void SetRefreshMinutes(int minutes)
    {
        _settings.RefreshMinutes = minutes;
        _settings.Save();
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMinutes(minutes);
        _refreshTimer.Start();
        Render();
    }

    private void ShowAbout()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        string text =
            $"{DisplayName} {version}\n\n" +
            "Claude Code CLI의 /usage 결과로 플랜 사용량 한도를 보여주는 가젯입니다.\n\n" +
            "Copyright (c) 2026 GREAT PEACE\n" +
            "MIT License\n" +
            "이 프로그램은 \"있는 그대로\" 제공되며, 어떠한 보증도 하지 않습니다. " +
            "사용으로 인한 문제에 대해 저작자는 책임지지 않습니다.\n\n" +
            "AI Usage는 Anthropic과 관련 없는 비공식 도구입니다. " +
            "\"Claude\"와 \"Claude Code\"는 Anthropic의 상표입니다.";

        // 숨겨진 상태에서는 소유자 없이 띄운다
        if (IsVisible)
            MessageBox.Show(this, text, $"{DisplayName} 정보", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(text, $"{DisplayName} 정보", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        string text = DisplayName;
        if (_last is { Windows.Count: > 0 })
        {
            var parts = new List<string>();
            foreach (var w in _last.Windows)
                parts.Add($"{w.Name} {(IsExpired(w) ? 0 : w.Percent):0}%");
            text = DisplayName + " · " + string.Join(" / ", parts);
        }
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ToggleTopmost()
    {
        Topmost = !Topmost;
        _settings.Topmost = Topmost;
        _settings.Save();
    }

    private void ToggleVisible()
    {
        if (IsVisible)
            HideGadget();
        else
            ShowGadget();
    }

    private void HideGadget()
    {
        Hide();
        _tray?.ShowBalloonTip(4000, DisplayName,
            "알림 영역(트레이) 아이콘으로 숨겼습니다.\n아이콘을 클릭하거나 프로그램을 다시 실행하면 나타납니다.",
            Forms.ToolTipIcon.Info);
    }

    private void ShowGadget()
    {
        if (_exiting) return;
        Show();
        Activate();
    }

    private void ResetPosition()
    {
        _settings.Left = null;
        _settings.Top = null;
        _settings.Save();
        ShowGadget();
        CenterOnPrimaryScreen();
    }

    private static bool AutoStart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value)
                key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(AppName, false);
        }
    }

    /// <summary>트레이 아이콘: 어두운 원 위에 파란 게이지 호.</summary>
    private static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(34, 34, 34));
            g.FillEllipse(background, 0, 0, 31, 31);
            using var track = new Drawing.Pen(Drawing.Color.FromArgb(95, 95, 95), 5);
            g.DrawEllipse(track, 6, 6, 19, 19);
            using var arc = new Drawing.Pen(Drawing.Color.FromArgb(76, 141, 246), 5)
            {
                StartCap = Drawing.Drawing2D.LineCap.Round,
                EndCap = Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawArc(arc, 6, 6, 19, 19, -90, 250);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    // ───── 종료 ─────

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 등으로 닫으면 트레이로 숨김
        if (!_exiting)
        {
            e.Cancel = true;
            HideGadget();
        }
        base.OnClosing(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        _refreshTimer.Stop();
        _tickTimer.Stop();
        _showWait?.Unregister(null);
        _showEvent.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _settings.Save();
        Close();
        Application.Current.Shutdown();
    }
}
