"""Isolated ConPTY + real Hyperlight child, model retry and double-Escape acceptance."""
import json
import os
import queue
import re
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import pyte
from winpty import PtyProcess

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/qa/child-runtime" / uuid.uuid4().hex
workspace = output / "workspace"
workspace.mkdir(parents=True)
(workspace / "readproof.txt").write_text("CHILD-FILE-RESULT", encoding="utf-8")
release = threading.Event()
requests = []
counts = {"parent": 0, "child": 0}
lock = threading.Lock()

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        child = any("\nYou are a delegated agent." in str(m.get("content", "")) for m in request["messages"] if m["role"] == "system")
        kind = "child" if child else "parent"
        with lock:
            counts[kind] += 1
            index = counts[kind]
            requests.append({"kind": kind, "index": index})
        if not child and index == 1:
            body = b"fixture transient outage"
            self.send_response(503); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
            return
        self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
        def event(value):
            self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode()); self.wfile.flush()
        def code(value, call_id):
            event({"choices": [{"delta": {"tool_calls": [{"index": 0, "id": call_id, "type": "function", "function": {"name": "execute_code", "arguments": json.dumps({"code": value})}}]}}]})
        try:
            finish = "stop"
            if child and index == 1:
                code('r = call_tool("file_read", path="readproof.txt")\nprint(r)', "child-code")
                finish = "tool_calls"
            elif child:
                event({"choices": [{"delta": {"content": "CHILD-LIVE-PROOF"}}]}); release.wait(90)
            elif index == 2:
                code('print(call_tool("subagent", agent="explore", description="CHILD-DESCRIPTION-PROOF", prompt="CHILD-REQUEST-PROOF", background=True))', "parent-code")
                finish = "tool_calls"
            elif index == 3:
                event({"choices": [{"delta": {"content": "PARENT-READY-PROOF"}}]})
            else:
                event({"choices": [{"delta": {"content": "CANCEL-HOLD-PROOF"}}]}); release.wait(90)
            event({"choices": [{"delta": {}, "finish_reason": finish}]})
            self.wfile.write(b"data: [DONE]\n\n"); self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
server.daemon_threads = True
threading.Thread(target=server.serve_forever, daemon=True).start()
session, config = str(uuid.uuid4()), str(uuid.uuid4())
(output / "allagent.json").write_text(json.dumps({"conversations": [{"id": session, "title": "MAIN-HISTORY-PROOF", "messages": []}],
    "apiConfigs": [{"id": config, "providerId": "openai", "displayName": "fixture", "protocol": "openai", "baseUrl": f"http://127.0.0.1:{server.server_port}", "defaultModel": "fixture", "models": ["fixture"], "isEnabled": True}],
    "skills": [], "appSettings": {}, "mcpServerConfigs": []}), encoding="utf-8")
env = os.environ.copy(); env.pop("NO_COLOR", None)
env.update({"TERM": "xterm-256color", "FORCE_COLOR": "1", "COLORTERM": "truecolor"})
exe = os.environ.get("ALLAGENT_DOTNET_QA_EXECUTABLE", str(root / "build/interaction-verification/terminal/allagent.exe"))
proc = PtyProcess.spawn([exe, "chat", "--workspace", str(workspace), "--data-dir", str(output), "--resume", session, "--config", config], cwd=str(root), env=env, dimensions=(42, 140))
chunks, raw = queue.Queue(), []
screen = pyte.Screen(140, 42); stream = pyte.Stream(screen)
sgr_pending = ""
def read():
    try:
        while True: chunks.put(proc.read(65536))
    except Exception: pass
threading.Thread(target=read, daemon=True).start()
def text(): return "\n".join("".join(screen.buffer[y][x].data for x in range(screen.columns)) for y in range(screen.lines))
def wait_for(predicate, seconds=35):
    global sgr_pending
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        try:
            chunk = chunks.get(timeout=0.05); raw.append(chunk)
            # This acceptance checks text/control flow, not colors. Avoid feeding
            # thousands of RGB SGR sequences per frame through pyte's slow parser.
            value = sgr_pending + chunk; sgr_pending = ""
            partial = re.search(r"\x1b(?:\[[0-9;:]*)?$", value)
            if partial: sgr_pending = value[partial.start():]; value = value[:partial.start()]
            stream.feed(re.sub(r"\x1b\[[0-9;:]*m", "", value))
        except queue.Empty: pass
        if predicate(text()): return
    raise AssertionError(f"Terminal condition timed out; queue={chunks.qsize()}, chunks={len(raw)}:\n" + text())
try:
    wait_for(lambda value: "输入任务" in value)
    proc.write("SPAWN-PROOF\r")
    wait_for(lambda value: "重试" in value)
    wait_for(lambda value: "PARENT-READY-PROOF" in value)
    proc.write("\x05")
    wait_for(lambda value: "子代理消息" in value and "CHILD-LIVE-PROOF" in value)
    assert "CHILD-REQUEST-PROOF" in text() and "readproof.txt" in text(), text()
    (output / "child-screen.txt").write_text(text(), encoding="utf-8")
    proc.write("\x1b")
    wait_for(lambda value: "子代理消息" not in value)
    proc.write("CANCEL-PROOF\r")
    wait_for(lambda value: "CANCEL-HOLD-PROOF" in value)
    proc.write("\x1b")
    wait_for(lambda value: "再次按 Esc 确认停止" in value, 4)
    assert "CANCEL-HOLD-PROOF" in text(), "First Escape unexpectedly cancelled execution"
    proc.write("\x1b")
    wait_for(lambda value: "已取消" in value, 5)
    (output / "cancel-screen.txt").write_text(text(), encoding="utf-8")
    saved = json.loads((output / "allagent.json").read_text(encoding="utf-8-sig"))
    children = [c for c in saved["conversations"] if c.get("parentConversationId") == session]
    assert len(children) == 1
    assert counts["child"] == 2, counts
    assert counts["parent"] == 4, counts
    print(json.dumps({"ok": True, "checks": ["real HTTP 503 retry", "real Hyperlight child file read", "child messages and live output in inspector", "one Escape closes inspector", "first Escape requests cancellation confirmation", "second Escape cancels model stream", "one child session linked to parent"], "requests": counts, "artifacts": str(output)}))
finally:
    release.set()
    proc.terminate(force=True) if proc.isalive() else None
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    (output / "requests.json").write_text(json.dumps(requests), encoding="utf-8")
    if proc.isalive(): proc.terminate(force=True)
    server.shutdown()
