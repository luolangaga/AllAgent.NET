"""ConPTY + actual Hyperlight + local SSE fixture: queue management and steer."""
import base64
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import queue
import threading
import time
import uuid
import pyte
from winpty import PtyProcess

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/qa/inbox" / uuid.uuid4().hex
output.mkdir(parents=True)
release = threading.Event()
started = threading.Event()
requests = []
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass
    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        requests.append(request)
        number = len(requests)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        def event(value):
            self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode())
            self.wfile.flush()
        event({"choices": [{"delta": {"content": "FIRST-RUN-PROOF" if number == 1 else "STEER-RUN-PROOF"}}]})
        if number == 1:
            started.set()
            release.wait(50)
        event({"choices": [{"delta": {}, "finish_reason": "stop"}]})
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()
server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
session, config = str(uuid.uuid4()), str(uuid.uuid4())
(output / "lanagent.json").write_text(json.dumps({"conversations": [{"id": session, "title": "Inbox fixture", "messages": []}],
    "apiConfigs": [{"id": config, "providerId": "openai", "displayName": "fixture", "protocol": "openai",
        "baseUrl": "http://127.0.0.1:" + str(server.server_port), "defaultModel": "fixture", "models": ["fixture"], "isEnabled": True}],
    "skills": [], "appSettings": {}, "mcpServerConfigs": []}), encoding="utf-8")
image = output / "correction.png"
png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jI1kAAAAASUVORK5CYII="
image.write_bytes(base64.b64decode(png))
proc = PtyProcess.spawn([str(root / "src/LanAgent.Terminal/bin/Release/net8.0/lanagent.exe"),
    "chat", "--workspace", str(root), "--data-dir", str(output), "--resume", session, "--config", config], cwd=str(root), dimensions=(36, 120))
chunks, raw = queue.Queue(), []
screen = pyte.Screen(120, 36)
stream = pyte.Stream(screen)
def screen_text():
    return "\n".join("".join(screen.buffer[y][x].data for x in range(screen.columns)) for y in range(screen.lines))
def read_state():
    try:
        return json.loads((output / "lanagent.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}
def pending():
    return read_state().get("pendingInputs", [])
def reader():
    try:
        while proc.isalive():
            chunks.put(proc.read(65536))
    except (EOFError, OSError):
        pass
threading.Thread(target=reader, daemon=True).start()
def wait_for(check, timeout=40):
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
    raise AssertionError("Inbox acceptance timed out\n" + screen_text())
def send(command):
    proc.write("\x1b[200~" + command + "\x1b[201~\r")
try:
    wait_for(lambda text: "F1" in text)
    send("Initial task")
    wait_for(lambda text: started.is_set())
    send('CORRECTION @"' + str(image) + '"')
    wait_for(lambda text: len(pending()) == 1)
    correction = pending()[0]
    image.unlink()
    send("KEEP-DRAFT")
    wait_for(lambda text: len(pending()) == 2)
    draft = pending()[1]
    send("DELETE-ME")
    wait_for(lambda text: len(pending()) == 3)
    deleted = pending()[2]
    send("/queue delete " + deleted["id"][:8])
    wait_for(lambda text: len(pending()) == 2)
    send("/queue list")
    wait_for(lambda text: "待处理输入" in text and draft["id"][:8] in text)
    proc.write("UNSENT-PREFIX\x18q")
    wait_for(lambda text: "Ctrl+U/Z 恢复草稿" in text)
    proc.write("\x1b[B\x15")
    wait_for(lambda text: len(pending()) == 1 and "KEEP-DRAFT" in text and "UNSENT-PREFIX" in text)
    (output / "edited-draft-screen.txt").write_text(screen_text(), encoding="utf-8")
    # Preserve the restored draft while moving the durable correction into steer delivery.
    proc.write("\x01")
    send("/queue")
    wait_for(lambda text: "Ctrl+U/Z 恢复草稿" in text)
    proc.write("\r")
    wait_for(lambda text: len(pending()) == 1 and pending()[0]["delivery"] == "steer")
    release.set()
    wait_for(lambda text: len(requests) == 2 and not pending())
    image_blocks = [part for message in requests[1]["messages"] if isinstance(message.get("content"), list)
        for part in message["content"] if part.get("type") == "image_url"]
    assert len(image_blocks) == 1 and image_blocks[0]["image_url"]["url"] == "data:image/png;base64," + png
    assert "KEEP-DRAFT" not in json.dumps(requests) and "DELETE-ME" not in json.dumps(requests) and "UNSENT-PREFIX" not in json.dumps(requests)
    wait_for(lambda text: "完成" in text)
    (output / "steer-screen.txt").write_text(screen_text(), encoding="utf-8")
    (output / "requests.json").write_text(json.dumps(requests, indent=2), encoding="utf-8")
    proc.terminate(force=True)
    print(json.dumps({"ok": True, "checks": ["durable queue", "queue list", "delete", "Ctrl+X,Q queue dialog and Ctrl+U restore into existing draft", "dialog Enter steer during active response", "snapshot image after original deletion", "queue management excluded from model requests"], "artifacts": str(output)}))
finally:
    release.set()
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    if proc.isalive():
        proc.terminate(force=True)
    server.shutdown()
