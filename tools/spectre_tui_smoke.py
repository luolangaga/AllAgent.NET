"""Windows ConPTY regression checks. Uses an isolated store; never calls a model."""
import json
import os
from pathlib import Path
import queue
import threading
import time
import uuid

import pyte
from winpty import PtyProcess

root = Path(__file__).resolve().parents[1]
output = root / "artifacts" / "qa" / "spectre-migration"
data = output / ("store-" + uuid.uuid4().hex)
data.mkdir(parents=True)
session = str(uuid.uuid4())
messages = [
    {"id": str(uuid.uuid4()), "conversationId": session, "role": "assistant",
     "content": f"消息 {index:03d} 中文历史内容\n" + "render-cache " * 12}
    for index in range(150)
]
(data / "allagent.json").write_text(json.dumps({
    "conversations": [{"id": session, "title": "Spectre 回归测试", "messages": messages}],
    "apiConfigs": [], "skills": [], "appSettings": {}, "mcpServerConfigs": []
}, ensure_ascii=False), encoding="utf-8")
environment = os.environ.copy()
environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
process = PtyProcess.spawn([
    str(root / "src/AllAgent.NET.Terminal/bin/Debug/net10.0/allagent.exe"), "chat",
    "--workspace", str(root), "--data-dir", str(data), "--resume", session
], cwd=str(root), env=environment, dimensions=(30, 100))
chunks = queue.Queue()
raw = []
received = []
screen = pyte.Screen(100, 30)
stream = pyte.Stream(screen)

def read():
    try:
        while process.isalive():
            chunk = process.read(65536)
            chunks.put((time.monotonic(), chunk))
    except (EOFError, OSError):
        pass

threading.Thread(target=read, daemon=True).start()

def drain(seconds=0.5):
    until = time.monotonic() + seconds
    size = 0
    while time.monotonic() < until:
        try:
            received_at, chunk = chunks.get(timeout=0.03)
            received.append(received_at)
            raw.append(chunk)
            size += len(chunk)
            if "\x1b[c" in chunk:
                process.write("\x1b[?1;2c")
            if "\x1b[6n" in chunk:
                process.write("\x1b[1;1R")
            if "\x1b[18t" in chunk:
                process.write(f"\x1b[8;{screen.lines};{screen.columns}t")
            stream.feed(chunk)
        except queue.Empty:
            pass
    return size

def snapshot(name):
    text = "\n".join(screen.display)
    (output / (name + ".txt")).write_text(text, encoding="utf-8")
    return text

def key(virtual_key, character=0, control_state=0):
    # .NET's Windows console enables the Win32 input protocol under ConPTY.
    process.write(f"\x1b[{virtual_key};0;{character};1;{control_state};1_\x1b[{virtual_key};0;{character};0;{control_state};1_")

def text(value):
    encoded = value.encode("utf-16-le")
    events = []
    for index in range(0, len(encoded), 2):
        character = int.from_bytes(encoded[index:index + 2], "little")
        virtual_key = 27 if character == 27 else 13 if character in (10, 13) else 0
        events.append(f"\x1b[{virtual_key};0;{character};1;0;1_\x1b[{virtual_key};0;{character};0;0;1_")
    process.write("".join(events))

def wait_for(predicate, timeout=3):
    started = time.monotonic()
    while time.monotonic() - started < timeout:
        drain(0.03)
        visible = "\n".join(screen.display)
        if predicate(visible):
            return time.monotonic() - started
    raise AssertionError("\n".join(screen.display))

checks = []
try:
    drain(5)
    initial = snapshot("history")
    assert "消息 149" in initial and "F1 菜单" in initial, initial
    # pyte does not emulate joined emoji reliably; unit tests cover those graphemes.
    text("\x1b[200~中文草稿\n第二行\x1b[201~")
    paste_latency = wait_for(lambda visible: "中文草稿" in visible and "第二行" in visible)
    snapshot("draft")
    drain(0.3)
    before_scroll = time.monotonic()
    key(33)
    scroll_latency = wait_for(lambda visible: "阅读历史" in visible and "消息 149" not in visible)
    snapshot("scrolled")
    first_output = next(moment for moment in received if moment > before_scroll) - before_scroll
    checks.append(f"150-message history scroll: first output {first_output:.2f}s; full capture {scroll_latency:.2f}s")
    key(112)
    wait_for(lambda visible: "操作菜单" in visible)
    snapshot("menu")
    key(27, 27)
    wait_for(lambda visible: "中文草稿" in visible and "第二行" in visible and "F1 菜单" in visible)
    snapshot("restored-draft")
    process.write("x")
    wait_for(lambda visible: "第二行x" in visible)
    checks.append(f"native menu Escape restores draft and input; paste visible after {paste_latency:.2f}s")
    drain(0.3)
    process.setwinsize(24, 80)
    screen.resize(lines=24, columns=80)
    # Wait for a complete frame at the new width, rather than a partially reflowed old frame.
    wait_for(lambda visible: "中文草稿" in visible and "第二行x" in visible and "F1 菜单" in screen.display[-1] and "会话信息" not in visible and "C:\\Users" in screen.display[1], timeout=10)
    drain(2)
    resized = snapshot("resized")
    assert "中文草稿" in resized and "第二行x" in resized and "F1 菜单" in screen.display[-1] and "会话信息" not in resized, resized
    checks.append("resize keeps input and footer visible")
    drain(1.5)
    idle_bytes = drain(0.6)
    assert idle_bytes < 500, idle_bytes
    checks.append(f"idle terminal output: {idle_bytes} characters / 0.6s")
    print(json.dumps({"passed": checks, "snapshots": str(output)}, ensure_ascii=False))
finally:
    (output / "terminal.ansi").write_text("".join(raw), encoding="utf-8")
    if process.isalive():
        process.terminate(force=True)
