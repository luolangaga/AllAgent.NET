"""ConPTY timeline search/jump and fork-before with restored image draft; no model calls."""
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
output = root / "artifacts/qa/timeline" / uuid.uuid4().hex
output.mkdir(parents=True)
session = str(uuid.uuid4())
png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jI1kAAAAASUVORK5CYII="
messages = []
target = None
for index in range(18):
    message = {"id": str(uuid.uuid4()), "conversationId": session, "role": "user", "content": f"HISTORY-{index:02d}-PROOF " + "界" * 45,
        "createdAt": f"2026-10-11T01:{index:02d}:00Z"}
    if index == 4:
        target = message["id"]
        message["inputText"] = "HISTORY-04-PROOF @deleted.png"
        message["images"] = [{"name": "deleted.png", "mediaType": "image/png", "base64": png}]
    messages.append(message)
    messages.append({"id": str(uuid.uuid4()), "conversationId": session, "role": "assistant", "content": "Reply " + str(index) + "\n" + "x" * 100,
        "createdAt": f"2026-10-11T01:{index:02d}:01Z"})
(output / "allagent.json").write_text(json.dumps({"conversations": [{"id": session, "title": "Timeline fixture", "messages": messages}],
    "apiConfigs": [], "skills": [], "appSettings": {}, "mcpServerConfigs": []}), encoding="utf-8")
proc = PtyProcess.spawn([str(root / "src/AllAgent.NET.Terminal/bin/Release/net10.0/allagent.exe"), "chat", "--workspace", str(root),
    "--data-dir", str(output), "--resume", session], cwd=str(root), dimensions=(30, 120))
chunks, raw = queue.Queue(), []
screen = pyte.Screen(120, 30)
stream = pyte.Stream(screen)
def screen_text():
    return "\n".join("".join(screen.buffer[y][x].data for x in range(screen.columns)) for y in range(screen.lines))
def state():
    try:
        return json.loads((output / "allagent.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}
def reader():
    try:
        while proc.isalive():
            chunks.put(proc.read(65536))
    except (EOFError, OSError):
        pass
threading.Thread(target=reader, daemon=True).start()
def wait_for(check, timeout=20):
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
    raise AssertionError("Timeline acceptance timed out\n" + screen_text())
def send(command):
    proc.write("\x1b[200~" + command + "\x1b[201~\r")
try:
    wait_for(lambda text: "HISTORY-17-PROOF" in text)
    send("/timeline jump " + target[:8])
    wait_for(lambda text: "HISTORY-04-PROOF" in text and "HISTORY-17-PROOF" not in text)
    (output / "jump-screen.txt").write_text(screen_text(), encoding="utf-8")
    proc.write("UNSENT-TIMELINE-DRAFT\x18g")
    wait_for(lambda text: "会话时间线" in text)
    proc.write("HISTORY-04\r")
    wait_for(lambda text: "消息操作" in text)
    proc.write("\r")
    wait_for(lambda text: "HISTORY-04-PROOF" in text and "会话时间线" not in text and "消息操作" not in text and "UNSENT-TIMELINE-DRAFT" in text)
    proc.write("\x01")
    send("/timeline restore " + target[:8])
    wait_for(lambda text: "图片 1" in text and "deleted.png" in text)
    (output / "restore-screen.txt").write_text(screen_text(), encoding="utf-8")
    proc.write("\x01")
    send("/fork --before " + target[:8])
    wait_for(lambda text: len(state().get("conversations", [])) == 2 and "图片 1" in text)
    data = state()
    original = next(c for c in data["conversations"] if c["id"] == session)
    fork = next(c for c in data["conversations"] if c["id"] != session)
    assert len(original["messages"]) == 36 and len(fork["messages"]) == 8
    assert all("HISTORY-04" not in m["content"] for m in fork["messages"])
    assert all(m["conversationId"] == fork["id"] and m["id"] not in {old["id"] for old in original["messages"]} for m in fork["messages"])
    assert next(m for m in original["messages"] if m["id"] == target)["images"][0]["base64"] == png
    (output / "fork-screen.txt").write_text(screen_text(), encoding="utf-8")
    proc.write("\x01")
    proc.terminate(force=True)
    print(json.dumps({"ok": True, "checks": ["message jump", "Ctrl+X,G timeline search and jump action", "existing unsent draft preserved", "image draft restoration", "fork before selected user", "unsent fork draft", "original history intact"], "artifacts": str(output)}))
finally:
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    if proc.isalive():
        proc.terminate(force=True)
