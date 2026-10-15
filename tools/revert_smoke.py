"""ConPTY staged multi-turn message/file revert and cancellation across restart."""
import base64
import json
from pathlib import Path
import queue
import threading
import time
import uuid
import pyte
from winpty import PtyProcess

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/qa/revert" / uuid.uuid4().hex
workspace, data = output / "workspace", output / "data"
workspace.mkdir(parents=True)
data.mkdir()
session = str(uuid.uuid4())
messages = [{"id": str(uuid.uuid4()), "conversationId": session, "role": "user", "content": "BASELINE-PROOF", "createdAt": "2026-10-11T01:00:00Z"}]
users, checkpoints = [], []
for index in range(1, 4):
    before = json.loads(json.dumps(messages))
    user = {"id": str(uuid.uuid4()), "conversationId": session, "role": "user", "content": f"TURN-{index}-PROOF", "createdAt": f"2026-10-11T01:0{index}:00Z"}
    users.append(user)
    messages.extend([user, {"id": str(uuid.uuid4()), "conversationId": session, "role": "assistant", "content": f"REPLY-{index}-PROOF", "createdAt": f"2026-10-11T01:0{index}:01Z"}])
    def snapshot_message(m):
        return {k[0].upper() + k[1:]: v for k, v in m.items()}
    def encoded(text):
        return base64.b64encode(text.encode()).decode()
    files = [{"Path": "shared.txt", "Before": encoded("original" if index == 1 else "version " + str(index - 1)), "After": encoded("version " + str(index)), "Completed": True}]
    if index == 1:
        files.append({"Path": "created.txt", "Before": None, "After": encoded("created"), "Completed": True})
    checkpoints.append({"Id": str(uuid.uuid4()), "SessionId": session, "CreatedAt": f"2026-10-11T01:0{index}:00Z", "Label": "turn " + str(index),
        "Files": files, "BeforeMessages": [snapshot_message(m) for m in before], "AfterMessages": [snapshot_message(m) for m in messages]})
import hashlib
key = hashlib.sha256(str(workspace).upper().encode()).hexdigest().upper()[:24]
storage = data / "checkpoints" / key
storage.mkdir(parents=True)
for checkpoint in checkpoints:
    (storage / (checkpoint["Id"].replace("-", "") + ".json")).write_text(json.dumps(checkpoint), encoding="utf-8")
(data / "lanagent.json").write_text(json.dumps({"conversations": [{"id": session, "title": "Revert fixture", "messages": messages}], "apiConfigs": [],
    "skills": [], "appSettings": {}, "mcpServerConfigs": []}), encoding="utf-8")
(workspace / "shared.txt").write_text("version 3", encoding="utf-8")
(workspace / "created.txt").write_text("created", encoding="utf-8")
raw = []
def state():
    try:
        return json.loads((data / "lanagent.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}
class Terminal:
    def __init__(self):
        self.proc = PtyProcess.spawn([str(root / "src/LanAgent.Terminal/bin/Release/net8.0/lanagent.exe"), "chat", "--workspace", str(workspace), "--data-dir", str(data), "--resume", session], cwd=str(root), dimensions=(32, 120))
        self.chunks = queue.Queue()
        self.screen = pyte.Screen(120, 32)
        self.stream = pyte.Stream(self.screen)
        threading.Thread(target=self.reader, daemon=True).start()
    def reader(self):
        try:
            while self.proc.isalive():
                self.chunks.put(self.proc.read(65536))
        except (EOFError, OSError):
            pass
    def text(self):
        return "\n".join("".join(self.screen.buffer[y][x].data for x in range(self.screen.columns)) for y in range(self.screen.lines))
    def wait(self, check, timeout=20):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            try:
                chunk = self.chunks.get(timeout=0.1)
                raw.append(chunk)
                self.stream.feed(chunk)
            except queue.Empty:
                pass
            if check(self.text()):
                return
        raise AssertionError("Revert acceptance timed out\n" + self.text())
    def send(self, command):
        self.proc.write("\x1b[200~" + command + "\x1b[201~\r")
    def close(self):
        self.proc.write("\x01")
        self.proc.terminate(force=True)
        end = time.monotonic() + 10
        while self.proc.isalive() and time.monotonic() < end:
            time.sleep(0.1)
        if self.proc.isalive():
            self.proc.terminate(force=True)
terminal = None
try:
    terminal = Terminal()
    terminal.wait(lambda text: "TURN-3-PROOF" in text)
    terminal.send("/timeline revert " + users[1]["id"][:8])
    terminal.wait(lambda text: "撤回 2 轮任务" in text)
    terminal.proc.write("\x1b[B\r")
    terminal.wait(lambda text: "已暂存撤回" in text and "TURN-2-PROOF" in text and "REPLY-2-PROOF" not in text and "TURN-3-PROOF" not in text)
    assert (workspace / "shared.txt").read_text() == "version 1" and len(state()["conversations"][0]["messages"]) == 7
    (output / "staged-screen.txt").write_text(terminal.text(), encoding="utf-8")
    terminal.close()
    terminal = Terminal()
    terminal.wait(lambda text: "已暂存撤回" in text and "TURN-2-PROOF" in text and "REPLY-2-PROOF" not in text and "TURN-3-PROOF" not in text)
    terminal.proc.write("\x01")
    terminal.send("/redo")
    terminal.wait(lambda text: "TURN-3-PROOF" in text and "已暂存撤回" not in text)
    assert (workspace / "shared.txt").read_text() == "version 3" and state()["conversations"][0].get("revert") is None
    (output / "restored-screen.txt").write_text(terminal.text(), encoding="utf-8")
    terminal.close()
    print(json.dumps({"ok": True, "checks": ["multi-turn revert confirmation", "file restoration", "history retained but hidden", "unsent reverted draft", "stage survives terminal restart", "redo clears stage and restores files/history"], "artifacts": str(output)}))
finally:
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    if terminal and terminal.proc.isalive():
        terminal.proc.terminate(force=True)
