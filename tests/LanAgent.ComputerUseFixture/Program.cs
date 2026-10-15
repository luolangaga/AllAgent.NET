namespace LanAgent.ComputerUseFixture;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new TestForm { Text = "LanAgent Computer Use Fixture", Width = 420, Height = 300 };
        var input = new TextBox { Name = "FixtureInput", AccessibleName = "Fixture input", Left = 20, Top = 20, Width = 320 };
        var output = new Label { Name = "FixtureResult", Text = "Waiting", Left = 20, Top = 100, Width = 320 };
        var button = new Button { Name = "FixtureApply", Text = "Apply", Left = 20, Top = 55 };
        button.Click += (_, _) => output.Text = "Applied: " + input.Text;
        var option = new CheckBox { Text = "Enable option", Name = "FixtureOption", Left = 20, Top = 130 };
        var rename = new Button { Text = "Rename target", Left = 20, Top = 165, Width = 150 };
        rename.Click += (_, _) => button.Text = "Apply changed";
        var readOnly = new TextBox { AccessibleName = "Readonly input", ReadOnly = true, Text = "Preserve me", Left = 20, Top = 205, Width = 320 };
        form.Controls.AddRange([input, button, output, option, rename, readOnly]);
        Application.Run(form);
    }
    private sealed class TestForm : Form { protected override bool ShowWithoutActivation => true; }
}
