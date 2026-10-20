"""Real-model kernel comparison. Uses the saved default config without exporting credentials.

Build the terminal first, then run: python tools/compare_agent_kernels.py
The six fixtures run twice on each kernel in alternating order. Results are assertions
over files and host events, never the model's claim that it succeeded. Workspaces remain
under artifacts/ for inspection; no existing data is deleted or migrated by this script.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]


def lower_keys(value):
    return {key.lower(): item for key, item in value.items()}


def default_config():
    if os.name == "nt":
        # Packaged Python installations can see a virtualized AppData directory.
        # Ask the native shell for only the three non-secret fields we need.
        script = "$d = Get-Content -Raw -LiteralPath (Join-Path $env:APPDATA 'AllAgent.NET/allagent.json') | ConvertFrom-Json; " \
                 "$id = $d.AppSettings.'terminal:lastConfig'; $c = $d.ApiConfigs | Where-Object { $_.Id -eq $id } | Select-Object -First 1; " \
                 "if ($null -eq $c) { exit 2 }; @{ id=$c.Id; providerid=$c.ProviderId; defaultmodel=$c.DefaultModel } | ConvertTo-Json -Compress"
        shell = Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        response = subprocess.run([str(shell), "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True, encoding="utf-8", check=True)
        return json.loads(response.stdout)
    path = Path(os.environ["APPDATA"]) / "AllAgent.NET/allagent.json"
    for attempt in range(20):
        try:
            data = lower_keys(json.loads(path.read_text(encoding="utf-8-sig")))
            break
        except (FileNotFoundError, PermissionError):
            if attempt == 19:
                raise
            time.sleep(0.1)
    selected = data.get("appsettings", {}).get("terminal:lastConfig")
    configurations = [lower_keys(item) for item in data.get("apiconfigs", [])]
    config = next((item for item in configurations if item.get("id") == selected), None)
    if not config:
        raise RuntimeError("No saved default model configuration; select a model in the terminal first.")
    return {key: config[key] for key in ("id", "providerid", "defaultmodel")}


CASES = ["aggregate", "edit", "recover", "skill", "parallel", "required-child"]


def fixture(workspace, case):
    workspace.mkdir(parents=True)
    def write(name, content):
        (workspace / name).write_text(content, encoding="utf-8")
    write("AGENTS.md", "Work only within this fixture. Preserve unrelated files. No network, external commands, commits or publication. Check every host result.\n")
    if case == "aggregate":
        write("numbers.csv", "value\n" + "\n".join(map(str, range(1, 31))))
        return 'Read numbers.csv and write result.json as {"sum":465}, calculating from the actual values. Check the written file. This is a small task; complete directly.'
    if case == "edit":
        write("calculator.py", "def add(a, b):\n    return a - b\n")
        return "Fix only calculator.py so add(a,b) adds instead of subtracting. Read and inspect the final code, preserve the function signature. No commands or child agents are needed."
    if case == "recover":
        write("backup.json", '{"values":[8,13,21]}')
        return 'First use file_read to inspect missing.json (it is deliberately absent); handle the failure without repeating that failed operation. Read backup.json and write result.json with the actual sum of values. Verify it. Use no child agents.'
    if case == "skill":
        skill = workspace / ".allagent/skills/bench-proof"
        skill.mkdir(parents=True)
        (skill / "SKILL.md").write_text('---\nname: bench-proof\ndescription: Fixture arithmetic protocol\n---\nRead rates.json and multiply its value by 7. Write result.json with the key answer. Verify the result with file_read.\n', encoding="utf-8")
        write("rates.json", '{"value":6}')
        return "Load the bench-proof instruction skill with skill_read and complete its arithmetic protocol. No commands or delegation."
    write("left.txt", "\n".join(map(str, range(1, 21))))
    write("right.txt", "\n".join(map(str, range(21, 41))))
    write("schema.json", '{"bias":5,"backup":[8,13,21]}')
    if case == "parallel":
        return ('Delegate two independent read-only explore tasks as background single calls, launched in a parallel batch: one sums left.txt and one sums right.txt. '
                'Use explicit contracts with requiredForCompletion=true and no editable paths. While they run, read schema.json yourself. '
                'Wait for both owned jobs with wait_agents, inspect their evidence and the source values, then write result.json with {"sum":<left plus right plus bias>}. '
                'Do not delegate your own schema/integration work, run commands, or start extra agents.')
    return ('Launch one explore background single task with a contract, requiredForCompletion=true, to inspect missing.json and return its blocker; no edits or follow-up retries. '
            'While it runs read schema.json and calculate the backup sum yourself. Wait for its final result, verify that the missing file failed, '
            'then write result.json with {"sum":42}. Do not repeat successful work, restart the child, or run commands.')


def verify(workspace, case, events, jobs):
    try:
        if case == "edit":
            import ast
            tree = ast.parse((workspace / "calculator.py").read_text(encoding="utf-8-sig"))
            function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "add")
            expression = function.body[0].value
            good = (len(tree.body) == 1 and len(function.body) == 1 and [arg.arg for arg in function.args.args] == ["a", "b"]
                    and isinstance(expression, ast.BinOp) and isinstance(expression.op, ast.Add)
                    and isinstance(expression.left, ast.Name) and expression.left.id == "a"
                    and isinstance(expression.right, ast.Name) and expression.right.id == "b")
        else:
            expected = {"aggregate": {"sum": 465}, "recover": {"sum": 42}, "skill": {"answer": 42}, "parallel": {"sum": 825}, "required-child": {"sum": 42}}[case]
            good = json.loads((workspace / "result.json").read_text(encoding="utf-8-sig")) == expected
        starts = [item["data"] for item in events if item.get("type") == "ToolCallStarted"]
        if case == "skill":
            good &= any(item.get("toolName") == "skill_read" and json.loads(item["argsJson"]).get("name") == "bench-proof" for item in starts)
        if case in ("parallel", "required-child"):
            good &= len(jobs) == (2 if case == "parallel" else 1) and all(job.get("state") in (4, "Completed") for job in jobs)
            good &= any(item.get("toolName") == "wait_agents" for item in starts)
        return bool(good)
    except (OSError, ValueError, KeyError, StopIteration, AttributeError):
        return False


def run(executable, output, config, case, kernel, repeat, timeout):
    workspace = output / f"{case}-{repeat}-{kernel}"
    prompt = fixture(workspace, case)
    env = os.environ.copy()
    env["ALLAGENT_AGENT_KERNEL"] = kernel
    command = [str(executable), "run", prompt, "--json", "--no-initialize", "--restricted", "--workspace", str(workspace),
               "--config", config["id"], "--timeout", str(timeout), "--code-timeout", "45", "--max-tools", "50"]
    start = time.monotonic()
    process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding="utf-8", errors="replace")
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout + 40)
    except subprocess.TimeoutExpired:
        timed_out = True
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True)
        else:
            process.kill()
        stdout, stderr = process.communicate()
    events = []
    for line in stdout.splitlines():
        try:
            event = json.loads(line)
            if isinstance(event, dict) and "type" in event:
                events.append(event)
        except ValueError:
            pass
    # Only fixture messages/host events are retained locally. Never copy provider config or stderr,
    # which can contain authentication diagnostics, into the shareable metrics report.
    (workspace / "events.json").write_text(json.dumps(events, ensure_ascii=False, indent=2), encoding="utf-8")
    job_path = workspace / ".allagent/data/background-tasks.json"
    jobs = [lower_keys(item) for item in json.loads(job_path.read_text(encoding="utf-8-sig"))] if job_path.exists() else []
    metrics = dict(case=case, kernel=kernel, repeat=repeat, elapsedSeconds=round(time.monotonic() - start, 3), exitCode=process.returncode,
                   timeout=timed_out, passed=process.returncode == 0 and verify(workspace, case, events, jobs), childCount=len(jobs),
                   inputTokens=sum(item["data"].get("inputTokens", 0) for item in events if item["type"] == "AgentTokenUsage") + sum(job.get("inputtokens", 0) for job in jobs),
                   outputTokens=sum(item["data"].get("outputTokens", 0) for item in events if item["type"] == "AgentTokenUsage") + sum(job.get("outputtokens", 0) for job in jobs),
                   modelSteps=sum(item["type"] == "AgentIteration" for item in events) + sum(job.get("modelsteps", 0) for job in jobs),
                   hostToolCalls=sum(item["type"] == "ToolCallStarted" and item["data"].get("toolName") != "execute_code" for item in events) + sum(job.get("hosttoolcalls", 0) for job in jobs),
                   codeExecutions=sum(item["type"] == "ToolCallStarted" and item["data"].get("toolName") == "execute_code" for item in events) + sum(job.get("codeexecutions", 0) for job in jobs),
                   finishReasons=[item["data"].get("finishReason") for item in events if item["type"] == "AgentDone"],
                   errorEvents=sum(item["type"] == "AgentError" for item in events))
    return metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, default=ROOT / "src/AllAgent.NET.Terminal/bin/Debug/net10.0/allagent.exe")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/agent-kernel-comparison" / uuid.uuid4().hex)
    parser.add_argument("--case", choices=CASES)
    parser.add_argument("--repeats", type=int, default=2)
    parser.add_argument("--timeout", type=int, default=180)
    args = parser.parse_args()
    config = default_config()
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"model": config["defaultmodel"], "provider": config["providerid"], "configId": config["id"],
              "executableSha256": hashlib.sha256(args.executable.read_bytes()).hexdigest(),
              "coreSha256": hashlib.sha256((args.executable.parent / "AllAgent.NET.Core.dll").read_bytes()).hexdigest(),
              "comparison": "Legacy vs framework loop with the same migrated host policies and prompts; modelSteps exclude retries and compaction requests.", "runs": []}
    for repeat in range(1, args.repeats + 1):
        for case in [args.case] if args.case else CASES:
            for kernel in (["legacy", "framework"] if repeat % 2 else ["framework", "legacy"]):
                result = run(args.executable.resolve(), args.output.resolve(), config, case, kernel, repeat, args.timeout)
                report["runs"].append(result)
                (args.output / "metrics.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(json.dumps(result), flush=True)
    print("Metrics: " + str(args.output.resolve() / "metrics.json"), flush=True)


if __name__ == "__main__":
    main()
