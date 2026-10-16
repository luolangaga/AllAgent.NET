"""Real ConPTY image paste, draft, removal and undo acceptance; no model calls."""
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
output = root / "artifacts/qa/image-draft" / uuid.uuid4().hex
output.mkdir(parents=True)
image = output / "pasted image.png"
image.write_bytes(base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jI1kAAAAASUVORK5CYII="))
proc = PtyProcess.spawn([str(root / "src/LanAgent.Terminal/bin/Release/net10.0/lanagent.exe"),
    "chat", "--workspace", str(root), "--data-dir", str(output)], cwd=str(root), dimensions=(30, 120))
chunks = queue.Queue()
raw = []
screen = pyte.Screen(120, 30)
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
try:
    wait_for(lambda text: "F1" in text)
    proc.write("Explain ")
    proc.write('\x1b[200~"' + str(image) + '"\x1b[201~')
    wait_for(lambda text: "图片 1" in text and "pasted image.png" in text)
    (output / "paste-screen.txt").write_text(screen_text(), encoding="utf-8")
    image.write_text("original file changed", encoding="utf-8")
    proc.write("\x1b\x7f")
    wait_for(lambda text: "已移除最后一张" in text and "图片 1" not in text and "Explain" in text)
    proc.write("\x1a")
    wait_for(lambda text: "图片 1" in text)
    (output / "undo-screen.txt").write_text(screen_text(), encoding="utf-8")
    assert "需要配置" not in screen_text(), "Paste must not submit task"
    proc.write("\x03")
    wait_for(lambda text: "图片 1" not in text)
    proc.write("ac\x1b[Db")
    wait_for(lambda text: "› abc" in text)
    proc.write("\x03\x1bOP")
    wait_for(lambda text: "继续聊天" in text)
    proc.write("\x1b")
    wait_for(lambda text: "继续聊天" not in text)
    proc.terminate(force=True)
    print(json.dumps({"ok": True, "checks": ["image path paste", "draft metadata", "Alt+Backspace removal", "Ctrl+Z undo", "paste without submit", "arrow editing", "F1 menu and Esc"], "artifacts": str(output)}))
finally:
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    if proc.isalive():
        proc.terminate(force=True)
