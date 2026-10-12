using System.Diagnostics;
using System.Text.Json;
using LanAgent.Core.Runtime;

namespace LanAgent.Core.CodeAct;

public sealed class PythonCodeValidator(string pythonPath)
{
    public async Task<string?> ValidateAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32000) return "Code must contain 1–32000 characters.";
        var info = BoundedProcess.StartInfo(pythonPath, ["-I", "-S", "-c", RuntimeScripts.Read("validate_code.py")]);
        info.RedirectStandardInput = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = info };
        process.Start();
        var output = BoundedProcess.DrainAsync(process.StandardOutput, 4000, timeout.Token);
        var error = BoundedProcess.DrainAsync(process.StandardError, 4000, timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { code, policy = PythonCodePolicy.Definition }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            var stderr = await error;
            if (process.ExitCode != 0 || result.Truncated) return "Code validation failed: " + stderr.Text;
            using var json = JsonDocument.Parse(result.Text);
            return json.RootElement.GetProperty("ok").GetBoolean() ? null : json.RootElement.GetProperty("error").GetString();
        }
        finally
        {
            BoundedProcess.Kill(process);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }
}
