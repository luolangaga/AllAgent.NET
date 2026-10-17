"""ConPTY navigation/export acceptance, isolated data and no model calls."""
import json
from pathlib import Path
import queue
import threading
import time
import uuid

import pyte
from winpty import PtyProcess

root = Path(__file__).resolve().parents[1]
output = root / "artifacts" / "qa" / "session-navigation" / uuid.uuid4().hex
output.mkdir(parents=True)
parent, child = str(uuid.uuid4()), str(uuid.uuid4())
def message(session, text):
    return {"id": str(uuid.uuid4()), "conversationId": session, "role": "assistant", "content": text}
(output / "allagent.json").write_text(json.dumps({
    "conversations": [
        {"id": parent, "title": "Parent fixture", "messages": [message(parent, "PARENT-NAVIGATION-PROOF")]},
        {"id": child, "parentConversationId": parent, "agentRole": "explore", "title": "Child fixture",
         "messages": [message(child, "CHILD-NAVIGATION-PROOF")]}
    ], "apiConfigs": [], "skills": [], "appSettings": {}, "mcpServerConfigs": []
}), encoding="utf-8")
proc = PtyProcess.spawn([str(root / "src/AllAgent.NET.Terminal/bin/Release/net10.0/allagent.exe"),
                         "chat", "--workspace", str(root), "--data-dir", str(output), "--resume", parent],
                        cwd=str(root), dimensions=(30, 100))
chunks = queue.Queue()
raw = []
screen = pyte.Screen(100, 30)
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
def wait_for(check, timeout=15):
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
    raise AssertionError("Terminal acceptance timed out\n" + screen_text())
def send(command):
    proc.write(command + "\r")
try:
    wait_for(lambda text: "PARENT-NAVIGATION-PROOF" in text)
    send("/children " + child[:8])
    wait_for(lambda text: "CHILD-NAVIGATION-PROOF" in text and "PARENT-NAVIGATION-PROOF" not in text)
    (output / "child-screen.txt").write_text(screen_text(), encoding="utf-8")
    send("/parent")
    wait_for(lambda text: "PARENT-NAVIGATION-PROOF" in text and "CHILD-NAVIGATION-PROOF" not in text)
    export = output / "transcript.md"
    send('/export "' + str(export) + '" --no-thinking --no-tools')
    wait_for(lambda text: export.exists() and "PARENT-NAVIGATION-PROOF" in export.read_text(encoding="utf-8"))
    content = export.read_text(encoding="utf-8")
    assert "PARENT-NAVIGATION-PROOF" in content and "CHILD-NAVIGATION-PROOF" not in content
    (output / "parent-screen.txt").write_text(screen_text(), encoding="utf-8")
    proc.terminate(force=True)
    print(json.dumps({"ok": True, "checks": ["child navigation", "parent navigation", "Markdown export"], "artifacts": str(output)}))
finally:
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    if proc.isalive():
        proc.terminate(force=True)

