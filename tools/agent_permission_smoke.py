"""ConPTY + actual Hyperlight: role ask requires Y/N even under full access."""
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import queue
import threading
import time
import uuid
import pyte
from winpty import PtyProcess

root = Path(__file__).resolve().parents[1]
executable = os.environ.get("LANAGENT_QA_EXECUTABLE", str(root / "src/LanAgent.Terminal/bin/Release/net8.0/lanagent.exe"))
output = root / "artifacts/qa/agent-permissions" / uuid.uuid4().hex
workspace = output / "workspace"
workspace.mkdir(parents=True)
(workspace / "allowed.txt").write_text("HUMAN-ALLOWED-PROOF", encoding="utf-8")
(workspace / "rejected.txt").write_text("HUMAN-REJECTED-MUST-NOT-LEAK", encoding="utf-8")
(workspace / ".env").write_text("RULE-DENIED-MUST-NOT-LEAK", encoding="utf-8")
requests, errors = [], []
code = '\n'.join([
    'print(call_tool("file_read", path=".env"))',
    'print(call_tool("file_read", path="allowed.txt"))',
    'print(call_tool("file_read", path="rejected.txt"))',
])

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass
    def do_POST(self):
        try:
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            requests.append(request)
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            def event(value):
                self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode())
                self.wfile.flush()
            if len(requests) == 1:
                event({"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "permission-proof", "type": "function", "function": {"name": "execute_code", "arguments": json.dumps({"code": code})}}]}}]})
            else:
                event({"choices": [{"delta": {"content": "PERMISSION-DONE-PROOF"}}]})
            event({"choices": [{"delta": {}, "finish_reason": "tool_calls" if len(requests) == 1 else "stop"}]})
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
        except Exception as exc:
            errors.append(str(exc))

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
session, parent, config = str(uuid.uuid4()), str(uuid.uuid4()), str(uuid.uuid4())
role = {"name": "reviewer", "description": "Review", "instructions": "Read the requested files.", "readOnly": True,
    "permissions": [{"action": "*", "resource": "*", "effect": "ask"}, {"action": "read", "resource": "*.env", "effect": "deny"}]}
(output / "lanagent.json").write_text(json.dumps({"conversations": [
    {"id": parent, "title": "Parent", "messages": []},
    {"id": session, "parentConversationId": parent, "agentRole": "reviewer", "title": "Permission fixture", "messages": []}],
    "apiConfigs": [{"id": config, "providerId": "openai", "displayName": "fixture", "protocol": "openai",
        "baseUrl": f"http://127.0.0.1:{server.server_port}", "defaultModel": "fixture", "models": ["fixture"], "isEnabled": True}],
    "skills": [], "appSettings": {"agents:roles": json.dumps([role])}, "mcpServerConfigs": []}), encoding="utf-8")
proc = PtyProcess.spawn([executable,
    "chat", "--workspace", str(workspace), "--data-dir", str(output), "--resume", session, "--config", config, "--full-access"], cwd=str(root), dimensions=(36, 120))
chunks, raw = queue.Queue(), []
screen = pyte.Screen(120, 36)
stream = pyte.Stream(screen)

def screen_text():
    return "\n".join("".join(screen.buffer[y][x].data for x in range(screen.columns)) for y in range(screen.lines))

def reader():
    try:
        while proc.isalive():
            chunks.put(proc.read(65536))
    except (EOFError, OSError):
        pass
threading.Thread(target=reader, daemon=True).start()

def wait_for(check, timeout=45):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            chunk = chunks.get(timeout=0.1)
            raw.append(chunk)
            stream.feed(chunk)
        except queue.Empty:
            pass
        if check(screen_text()):
            return
    raise AssertionError("Permission acceptance timed out\n" + screen_text())

try:
    wait_for(lambda text: "F1" in text)
    proc.write("\x1b[200~Read permission fixtures\x1b[201~\r")
    wait_for(lambda text: "allowed.txt" in text and "逐次确认" in text)
    (output / "allow-screen.txt").write_text(screen_text(), encoding="utf-8")
    assert len(requests) == 1, "Tool executed without human response"
    proc.write("y")
    wait_for(lambda text: "rejected.txt" in text and "逐次确认" in text)
    (output / "reject-screen.txt").write_text(screen_text(), encoding="utf-8")
    assert len(requests) == 1
    proc.write("n")
    wait_for(lambda text: len(requests) == 2 and "PERMISSION-DONE-PROOF" in text)
    tools = [m["content"] for m in requests[1]["messages"] if m["role"] == "tool"]
    assert any("HUMAN-ALLOWED-PROOF" in text and "permission rules deny" in text and "permission approval denied" in text for text in tools)
    assert not any("HUMAN-REJECTED-MUST-NOT-LEAK" in text or "RULE-DENIED-MUST-NOT-LEAK" in text for text in tools)
    assert all([tool["function"]["name"] for tool in request["tools"]] == ["execute_code"] for request in requests)
    assert not errors, errors
    wait_for(lambda text: "完成" in text)
    proc.write("/exit\r")
    print(json.dumps({"ok": True, "checks": ["actual resumed child microVM", "full-access does not skip role ask", "Y allows read", "N refuses read", "deny does not prompt", "denied contents excluded from model", "only execute_code exposed"], "artifacts": str(output)}))
finally:
    (output / "requests.json").write_text(json.dumps(requests, indent=2), encoding="utf-8")
    (output / "errors.json").write_text(json.dumps(errors), encoding="utf-8")
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    if proc.isalive():
        proc.terminate(force=True)
    server.shutdown()
