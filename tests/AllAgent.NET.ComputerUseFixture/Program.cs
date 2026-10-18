using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AllAgent.NET.ComputerUseFixture;

/// <summary>
/// UIA target for the computer-use worker: a WPF window whose element names, roles and
/// patterns match what the worker asserts (Edit "Fixture input", Button "Apply", CheckBox
/// "Enable option", read-only Edit "Readonly input" and the "Rename target" button).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = Build();
        application.MainWindow = window;
        // Create the window before pumping messages: callers wait with WaitForInputIdle,
        // which must not report idle before the automation surface exists.
        window.Show();
        application.Run();
    }

    private static Window Build()
    {
        var input = new TextBox { Width = 320, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(input, "Fixture input");
        var output = new TextBlock { Text = "Waiting", Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "Apply", Width = 90, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        apply.Click += (_, _) => output.Text = "Applied: " + input.Text;
        var option = new CheckBox { Content = "Enable option", Margin = new Thickness(0, 12, 0, 0) };
        var rename = new Button { Content = "Rename target", Width = 150, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        rename.Click += (_, _) => apply.Content = "Apply changed";
        var readOnly = new TextBox { Width = 320, Height = 26, IsReadOnly = true, Text = "Preserve me", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(readOnly, "Readonly input");

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(input);
        panel.Children.Add(apply);
        panel.Children.Add(output);
        panel.Children.Add(option);
        panel.Children.Add(rename);
        panel.Children.Add(readOnly);
        return new Window
        {
            Title = "AllAgent Computer Use Fixture",
            Width = 420, Height = 330,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = panel
        };
    }
}
