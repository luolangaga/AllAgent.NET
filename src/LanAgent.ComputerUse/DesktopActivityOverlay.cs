using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;
using Screen = System.Windows.Forms.Screen;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace LanAgent.ComputerUse;

// Visual adaptation of omni-computer-use's capsule and edge glow (MIT, Jason26214).
// Source pinned in THIRD-PARTY-NOTICES.md. Native WPF rendering, blue palette and
// persistent activity/idle protocol are LanAgent's implementation.
internal static class DesktopActivityOverlay
{
    internal const string WindowTitle = "LanAgent Computer Use Indicator";
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(240);

    internal static int Run(string mode, string? renderPath = null)
    {
        if (mode is not ("BackgroundOnly" or "ForegroundAllowed" or "IsolatedSession")) return 2;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new WpfApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (renderPath != null) { RenderPreview(renderPath); app.Shutdown(); return; }
                var windows = Screen.AllScreens.Select(screen => new IndicatorWindow(screen.Bounds)).ToArray();
                var visible = new Stopwatch();
                var closing = false;
                var exit = new DispatcherTimer();
                exit.Tick += (_, _) =>
                {
                    exit.Stop();
                    if (closing) { app.Shutdown(); return; }
                    closing = true;
                    foreach (var window in windows) window.FadeOut();
                    exit.Interval = FadeDuration; exit.Start();
                };
                void ScheduleExit(string state, bool pipeClosed)
                {
                    exit.Stop(); closing = false;
                    foreach (var window in windows) window.SetState(state);
                    var remaining = MinimumVisible - visible.Elapsed;
                    var delay = pipeClosed ? TimeSpan.Zero : ExitDelay;
                    exit.Interval = remaining > delay ? remaining : delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
                    exit.Start();
                }
                void Command(string command)
                {
                    if (command.StartsWith("active\t", StringComparison.Ordinal))
                    {
                        exit.Stop(); closing = false;
                        foreach (var window in windows) window.SetState("active");
                        Console.WriteLine("active"); Console.Out.Flush();
                    }
                    else if (command is "idle" or "stop") ScheduleExit(command, pipeClosed: false);
                }
                EventHandler? ready = null;
                ready = (_, _) =>
                {
                    if (windows.Any(window => !IsWindowVisible(new WindowInteropHelper(window).Handle))) return;
                    CompositionTarget.Rendering -= ready;
                    visible.Start();
                    Console.WriteLine("ready"); Console.Out.Flush();
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            string? command;
                            while ((command = Console.In.ReadLine()) != null)
                            {
                                var received = command;
                                app.Dispatcher.BeginInvoke(() => Command(received));
                            }
                        }
                        finally { app.Dispatcher.BeginInvoke(() => ScheduleExit("stop", pipeClosed: true)); }
                    });
                };
                foreach (var window in windows) window.Show();
                CompositionTarget.Rendering += ready;
                app.Run();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error == null) return 0;
        Console.Error.WriteLine(error.Message); return 1;
    }

    private static void RenderPreview(string path)
    {
        // Render the same production visual on a neutral backdrop; no user desktop pixels.
        var window = new IndicatorWindow(new System.Drawing.Rectangle(0, 0, 1600, 900));
        var root = (Grid)window.Content;
        root.Children.Insert(0, new Rectangle { Fill = Brush("#FF1C2533") });
        root.Measure(new Size(1600, 900)); root.Arrange(new Rect(0, 0, 1600, 900)); root.UpdateLayout();
        window.SetFrameSize(1600, 900, animate: false);
        var bitmap = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        window.Close();
    }

    private sealed class IndicatorWindow : Window
    {
        private readonly System.Drawing.Rectangle _bounds;
        private readonly Rectangle _runner;
        private readonly Grid _root;
        private readonly TextBlock _title;
        private readonly TextBlock _subtitle;

        public IndicatorWindow(System.Drawing.Rectangle bounds)
        {
            _bounds = bounds;
            Title = WindowTitle;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowActivated = false; ShowInTaskbar = false; Topmost = true;
            Focusable = false; IsHitTestVisible = false;
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            _root = new Grid { Background = new SolidColorBrush(Color.FromArgb(6, 174, 216, 252)) };
            Content = _root;
            AddEdgeGlow(_root);
            _root.Children.Add(new Rectangle
            {
                Margin = new Thickness(2), RadiusX = 12, RadiusY = 12,
                StrokeThickness = 1, Stroke = Brush("#6289C8F8")
            });
            _runner = new Rectangle
            {
                Margin = new Thickness(2), RadiusX = 12, RadiusY = 12,
                StrokeThickness = 1.8, Stroke = Brush("#DD91D3FF"),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            _root.Children.Add(_runner);
            _title = Text("AI正在操作你的电脑", 17, "#FF255579", FontWeights.SemiBold);
            _subtitle = Text("建议不要进行其他操作", 13, "#FF6D8597", FontWeights.Normal);
            _subtitle.Margin = new Thickness(0, 4, 0, 0);
            _root.Children.Add(CreateCapsule());
            SourceInitialized += (_, _) =>
            {
                var source = (HwndSource)PresentationSource.FromVisual(this);
                source.AddHook(WindowProc);
                var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
                SetWindowLongPtr(source.Handle, -20, (nint)(style | 0x08000000 | 0x00000020 | 0x00000080));
                Position(source.Handle);
            };
            Loaded += (_, _) => { Position(new WindowInteropHelper(this).Handle); AnimateFrame(); };
            SizeChanged += (_, _) => AnimateFrame();
        }

        private FrameworkElement CreateCapsule()
        {
            var panel = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(24, 24, 24, 64), Padding = new Thickness(24, 15, 28, 15),
                MaxWidth = 480, CornerRadius = new CornerRadius(40),
                Background = Brush("#FAEFF7FD"), BorderBrush = Brush("#D5C2DCEC"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Color.FromRgb(16, 35, 53), BlurRadius = 24, ShadowDepth = 4, Opacity = .18 }
            };
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition()); panel.Child = row;
            row.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, Fill = Brush("#FF64AEE5"),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
            });
            var text = new StackPanel(); Grid.SetColumn(text, 1);
            text.Children.Add(_title); text.Children.Add(_subtitle); row.Children.Add(text);
            return panel;
        }

        private static void AddEdgeGlow(Grid root)
        {
            // Adapt omni's alpha(d)=peak*(1-d/band)^1.3 falloff to retained WPF gradients.
            LinearGradientBrush Gradient(System.Windows.Point start, System.Windows.Point end)
            {
                var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
                foreach (var position in new[] { 0d, .2, .45, .7, 1 })
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(72 * Math.Pow(1 - position, 1.3)), 111, 190, 249), position));
                return brush;
            }
            root.Children.Add(new Rectangle { Height = 28, VerticalAlignment = VerticalAlignment.Top, Fill = Gradient(new(0, 0), new(0, 1)) });
            root.Children.Add(new Rectangle { Height = 28, VerticalAlignment = VerticalAlignment.Bottom, Fill = Gradient(new(0, 1), new(0, 0)) });
            root.Children.Add(new Rectangle { Width = 28, HorizontalAlignment = HorizontalAlignment.Left, Fill = Gradient(new(0, 0), new(1, 0)) });
            root.Children.Add(new Rectangle { Width = 28, HorizontalAlignment = HorizontalAlignment.Right, Fill = Gradient(new(1, 0), new(0, 0)) });
        }

        internal void SetState(string state)
        {
            _title.Text = state switch { "idle" => "本次电脑操作已结束", "stop" => "已停止电脑操作", _ => "AI正在操作你的电脑" };
            _subtitle.Text = state == "active" ? "建议不要进行其他操作" : "提示即将自动收起";
            _root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        }
        internal void FadeOut() => _root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, FadeDuration));

        private void AnimateFrame() => SetFrameSize(ActualWidth, ActualHeight, animate: true);
        internal void SetFrameSize(double width, double height, bool animate)
        {
            var perimeter = Math.Max(200, 2 * (width + height - 8) - (8 - 2 * Math.PI) * 12);
            var cycle = perimeter / _runner.StrokeThickness;
            var segment = Math.Min(160, cycle / 8);
            _runner.StrokeDashArray = new DoubleCollection { segment, cycle - segment };
            if (animate) _runner.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -cycle, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void Position(nint handle) => SetWindowPos(handle, -1, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, 0x0010 | 0x0040);
        private nint WindowProc(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
        {
            if (msg == 0x0084) { handled = true; return -1; }
            if (msg == 0x0021) { handled = true; return 3; }
            if (msg == 0x02E0) Dispatcher.BeginInvoke(() => Position(hwnd));
            return 0;
        }
    }

    private static TextBlock Text(string text, double size, string color, FontWeight weight) => new()
    { Text = text, FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = size, Foreground = Brush(color), FontWeight = weight, TextWrapping = TextWrapping.Wrap };
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(nint hwnd, int index, int value);
    private static nint GetWindowLongPtr(nint hwnd, int index) => nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);
    private static void SetWindowLongPtr(nint hwnd, int index, nint value)
    { if (nint.Size == 8) SetWindowLongPtr64(hwnd, index, value); else SetWindowLong32(hwnd, index, (int)value); }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
}
