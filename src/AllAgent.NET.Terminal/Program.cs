using AllAgent.NET.Diagnostics;
using AllAgent.NET.Terminal;

// The GUI host is a hidden child of the terminal: a crash there must surface as a dialog,
// while a console crash can point at the bundle and the GitHub submission command.
var mode = Environment.GetEnvironmentVariable("ALLAGENT_INTERNAL_TERMINAL_MODE");
var component = mode == "gui" ? "gui-host" : mode == "host" ? "terminal-host" : "terminal";
var guiHost = mode == "gui";
CrashReporter.Install(component, onReported: report => CrashNotice.Reported(report, guiHost));

try
{
    return await TerminalSession.RunAsync(args);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception ex)
{
    CrashReporter.Report(ex, component, fatal: true);
    return 1;
}
