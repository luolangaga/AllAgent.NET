using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Desktop;

internal static class AgentApprovalDialog
{
    public static async ValueTask<bool> ApproveAsync(ToolApprovalRequest request, CancellationToken ct) =>
        await ShowAsync($"Agent 授权 · {request.Name}", request.Arguments.GetRawText(), ct);
    public static async ValueTask<bool> InstallAsync(string message, CancellationToken ct) =>
        await ShowAsync("Agent 环境安装", message, ct);

    private static async Task<bool> ShowAsync(string title, string details, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null) { completion.TrySetResult(false); return; }
            var dialog = new Window { Title = title, Width = 640, Height = 360, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var allow = new Button { Content = "允许此次操作" };
            var deny = new Button { Content = "拒绝", IsDefault = true, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(deny); buttons.Children.Add(allow);
            var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20) };
            body.Children.Add(new TextBox { Text = details, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, AcceptsReturn = true });
            Grid.SetRow(buttons, 1); body.Children.Add(buttons); dialog.Content = body;
            allow.Click += (_, _) => { completion.TrySetResult(true); dialog.Close(); };
            deny.Click += (_, _) => dialog.Close();
            dialog.Closed += (_, _) => completion.TrySetResult(false);
            _ = dialog.ShowDialog(owner);
            var cancellation = ct.Register(() => Dispatcher.UIThread.Post(() => { completion.TrySetCanceled(ct); dialog.Close(); }));
            dialog.Closed += (_, _) => cancellation.Dispose();
        });
        return await completion.Task;
    }
}
