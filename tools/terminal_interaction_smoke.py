"""Real Windows PNG/DIB clipboard + ConPTY + Hyperlight + SSE terminal acceptance."""
import base64
import ctypes as c
from ctypes import wintypes as w
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import queue
import struct
import threading
import time
import uuid
import pyte
from winpty import PtyProcess

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/qa/terminal-interaction" / uuid.uuid4().hex
workspace = output / "workspace"
workspace.mkdir(parents=True)
for i in range(40): (workspace / f"file-{i:02}.txt").write_text("FILE-PICKER-PROOF", encoding="utf-8")
exe = os.environ.get("LANAGENT_QA_EXECUTABLE", str(root / "build/interaction-verification/terminal/lanagent.exe"))
png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jI1kAAAAASUVORK5CYII=")
u, k = c.windll.user32, c.windll.kernel32
u.OpenClipboard.argtypes = [w.HWND]
u.GetClipboardData.argtypes = [w.UINT]; u.GetClipboardData.restype = w.HANDLE
u.SetClipboardData.argtypes = [w.UINT, w.HANDLE]; u.SetClipboardData.restype = w.HANDLE
u.CreateWindowExW.argtypes = [w.DWORD, w.LPCWSTR, w.LPCWSTR, w.DWORD, c.c_int, c.c_int, c.c_int, c.c_int, w.HWND, w.HMENU, w.HINSTANCE, c.c_void_p]
u.CreateWindowExW.restype = w.HWND
k.GlobalAlloc.argtypes = [w.UINT, c.c_size_t]; k.GlobalAlloc.restype = w.HANDLE
k.GlobalLock.argtypes = [w.HANDLE]; k.GlobalLock.restype = c.c_void_p
k.GlobalUnlock.argtypes = [w.HANDLE]
k.GlobalSize.argtypes = [w.HANDLE]; k.GlobalSize.restype = c.c_size_t
k.GlobalFree.argtypes = [w.HANDLE]
u.RegisterClipboardFormatW.argtypes = [w.LPCWSTR]; u.RegisterClipboardFormatW.restype = w.UINT
owner = u.CreateWindowExW(0, "STATIC", "LanAgent QA clipboard", 0, 0, 0, 0, 0, -3, None, None, None)
assert owner
def opened():
    for _ in range(100):
        if u.OpenClipboard(owner): return
        time.sleep(0.01)
    raise AssertionError("Clipboard unavailable")
def read_format(fmt):
    # Bitmap/metafile clipboard values are GDI handles, not movable memory.
    if fmt in (2, 3, 14, 0x82, 0x83, 0x8E): return None
    handle = u.GetClipboardData(fmt)
    size = k.GlobalSize(handle) if handle else 0
    if not 0 < size < 32_000_000: return None
    pointer = k.GlobalLock(handle)
    if not pointer: return None
    try: return c.string_at(pointer, size)
    finally: k.GlobalUnlock(handle)
def replace_clipboard(formats):
    opened()
    try:
        assert u.EmptyClipboard()
        for fmt, data in formats:
            handle = k.GlobalAlloc(2, len(data)); assert handle
            pointer = k.GlobalLock(handle); assert pointer
            c.memmove(pointer, data, len(data)); k.GlobalUnlock(handle)
            if not u.SetClipboardData(fmt, handle):
                k.GlobalFree(handle)
                raise AssertionError("Could not set clipboard format")
    finally: u.CloseClipboard()
opened()
original = []
try:
    fmt = u.EnumClipboardFormats(0)
    while fmt:
        # Preserve global-memory formats in memory; never write user clipboard contents to artifacts.
        data = read_format(fmt)
        if data is not None: original.append((fmt, data))
        fmt = u.EnumClipboardFormats(fmt)
finally: u.CloseClipboard()
requests, errors = [], []
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_POST(self):
        try:
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"]))); requests.append(request)
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
            def event(value):
                self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode()); self.wfile.flush()
            if len(requests) == 1:
                code = 'print(call_tool("ask_user", question="CHOOSE-FEATURE-PROOF", choices=["A", "B", "C"], multiple=True))'
                event({"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "ask-proof", "type": "function", "function": {"name": "execute_code", "arguments": json.dumps({"code": code})}}]}}]})
            else: event({"choices": [{"delta": {"content": "INTERACTION-DONE-PROOF"}}]})
            event({"choices": [{"delta": {}, "finish_reason": "tool_calls" if len(requests) == 1 else "stop"}]})
            self.wfile.write(b"data: [DONE]\n\n"); self.wfile.flush()
        except Exception as exc: errors.append(str(exc))
server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
session, config = str(uuid.uuid4()), str(uuid.uuid4())
messages = [{"id": str(uuid.uuid4()), "conversationId": session, "role": "assistant", "content": "OLD-BLOCK-PROOF" if i == 0 else f"SCROLL-BLOCK-{i:03}", "createdAt": f"2026-10-11T10:{i:02}:00"} for i in range(40)]
(output / "lanagent.json").write_text(json.dumps({"conversations": [{"id": session, "title": "Interaction fixture", "messages": messages}],
    "apiConfigs": [{"id": config, "providerId": "openai", "displayName": "fixture", "protocol": "openai", "baseUrl": f"http://127.0.0.1:{server.server_port}", "defaultModel": "fixture", "models": ["fixture"], "isEnabled": True}],
    "skills": [], "appSettings": {}, "mcpServerConfigs": []}), encoding="utf-8")
qa_env = os.environ.copy()
qa_env.pop("NO_COLOR", None)
qa_env.update({"TERM": "xterm-256color", "FORCE_COLOR": "1", "COLORTERM": "truecolor"})
proc = PtyProcess.spawn([exe, "chat", "--workspace", str(workspace), "--data-dir", str(output), "--resume", session, "--config", config], cwd=str(root), env=qa_env, dimensions=(36, 120))
chunks, raw = queue.Queue(), []
screen = pyte.Screen(120, 36); stream = pyte.Stream(screen)
def text(): return "\n".join("".join(screen.buffer[y][x].data for x in range(screen.columns)) for y in range(screen.lines))
def reader():
    try:
        while proc.isalive(): chunks.put(proc.read(65536))
    except (EOFError, OSError): pass
threading.Thread(target=reader, daemon=True).start()
def wait_for(check, timeout=45):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            chunk = chunks.get(timeout=0.01); raw.append(chunk); stream.feed(chunk)
        except queue.Empty: pass
        if check(text()): return
    raise AssertionError("Interaction acceptance timed out\n" + text())
def right(): proc.write("\x1b[<2;6;31M\x1b[<2;6;31m")
try:
    wait_for(lambda value: "F1" in value)
    start = time.monotonic(); proc.write("\x1b[<64;8;5M" * 80)
    wait_for(lambda value: "OLD-BLOCK-PROOF" in value)
    wheel_ms = round((time.monotonic() - start) * 1000)
    (output / "scroll-screen.txt").write_text(text(), encoding="utf-8")
    proc.write("\x1b[1;5F"); wait_for(lambda value: "SCROLL-BLOCK-039" in value)
    row = next(y for y in range(screen.lines) if "SCROLL-BLOCK-037" in "".join(screen.buffer[y][x].data for x in range(screen.columns)))
    proc.write(f"\x1b[<0;4;{row+1}M\x1b[<0;4;{row+1}m" + "\x1b[A" * 10)
    wait_for(lambda value: "SCROLL-BLOCK-039" not in value)
    proc.write("\x1b[B" * 10); wait_for(lambda value: "SCROLL-BLOCK-039" in value)
    # Wait for an input acknowledgement after every arrow; a partially visible last
    # message can appear before all queued scroll keys have finished changing rows.
    proc.write("\x1b[200~FOCUS-BARRIER\x1b[201~")
    wait_for(lambda value: "FOCUS-BARRIER" in value)
    proc.write("\x01\x7f"); wait_for(lambda value: "FOCUS-BARRIER" not in value)
    row = next(y for y in range(screen.lines) if "SCROLL-BLOCK-037" in "".join(screen.buffer[y][x].data for x in range(screen.columns)))
    drag_start = time.monotonic()
    selection_before = screen.buffer[row][18].bg
    burst = "".join(f"\x1b[<32;{4+i%80};{row+1}M" for i in range(120))
    proc.write(f"\x1b[<0;4;{row+1}M" + burst + f"\x1b[<32;119;{row+7}M\x1b[<0;119;{row+7}m")
    wait_for(lambda value: any("SCROLL-BLOCK-037" in "".join(screen.buffer[y][x].data for x in range(screen.columns)) and screen.buffer[y][18].bg != selection_before for y in range(screen.lines)))
    drag_ms = round((time.monotonic() - drag_start) * 1000)
    right(); wait_for(lambda value: "已复制" in value)
    opened()
    try: assert read_format(13).decode("utf-16-le").rstrip("\0") == "SCROLL-BLOCK-037"
    finally: u.CloseClipboard()
    proc.write("\x1b[<0;6;31M\x1b[<0;6;31m")
    replace_clipboard([(13, ("RIGHT-PASTE-PROOF\0").encode("utf-16-le"))]); right()
    wait_for(lambda value: "RIGHT-PASTE-PROOF" in value)
    proc.write("\x01"); right()
    wait_for(lambda value: "已复制" in value)
    opened()
    try: assert read_format(13).decode("utf-16-le").rstrip("\0") == "RIGHT-PASTE-PROOF"
    finally: u.CloseClipboard()
    proc.write("\x1b"); time.sleep(0.07); proc.write("\x03")
    wait_for(lambda value: "RIGHT-PASTE-PROOF" not in value)
    proc.write("@file-")
    wait_for(lambda value: "file-00.txt" in value and "file-04.txt" in value)
    proc.write("\x1b[<65;6;25M")
    wait_for(lambda value: "› @file-03.txt" in value)
    row = next(y for y in range(screen.lines) if "file-03.txt" in "".join(screen.buffer[y][x].data for x in range(screen.columns)))
    proc.write(f"\x1b[<0;6;{row+1}M\x1b[<0;6;{row+1}m")
    wait_for(lambda value: "┃ › @file-03.txt" in value)
    (output / "file-selection-screen.txt").write_text(text(), encoding="utf-8")
    proc.write("\x03"); wait_for(lambda value: "┃ › @file-" not in value)
    replace_clipboard([(u.RegisterClipboardFormatW("PNG"), png)]); right()
    wait_for(lambda value: "图片 1" in value)
    dib = struct.pack("<IiiHHIIiiII", 40, 1, 1, 1, 32, 0, 4, 0, 0, 0, 0) + b"\xff\xff\xff\xff"
    replace_clipboard([(8, dib)])
    proc.write("\x16"); wait_for(lambda value: "图片 2" in value)
    replace_clipboard([(u.RegisterClipboardFormatW("PNG"), png)])
    proc.write("\x1b[200~\x1b[201~"); wait_for(lambda value: "图片 3" in value)
    (output / "images-screen.txt").write_text(text(), encoding="utf-8")
    proc.write("\r")
    wait_for(lambda value: "CHOOSE-FEATURE-PROOF" in value and "空格" in value)
    (output / "multi-question-screen.txt").write_text(text(), encoding="utf-8")
    proc.write(" \x1b[B \r")
    wait_for(lambda value: len(requests) == 2 and "INTERACTION-DONE-PROOF" in value)
    blocks = [part for message in requests[0]["messages"] if message["role"] == "user" and isinstance(message.get("content"), list) for part in message["content"] if part.get("type") == "image_url"]
    assert len(blocks) == 3
    assert all(blocks[index]["image_url"]["url"] == "data:image/png;base64," + base64.b64encode(png).decode() for index in (0, 2))
    bitmap = base64.b64decode(blocks[1]["image_url"]["url"].split(",", 1)[1])
    assert bitmap.startswith(b"\x89PNG\r\n\x1a\n") and struct.unpack(">II", bitmap[16:24]) == (1, 1)
    results = [json.loads(m["content"]) for m in requests[1]["messages"] if m["role"] == "tool"]
    assert any('["A","B"]' in json.dumps(result).replace('\\"', '"').replace(' ', '') for result in results), results
    assert not errors, errors
    wait_for(lambda value: "完成" in value)
    proc.write("\x1b[200~/spinner Dots\x1b[201~\r"); wait_for(lambda value: "加载指示已保存" in value)
    saved = json.loads((output / "lanagent.json").read_text(encoding="utf-8-sig"))
    assert saved["appSettings"]["terminal:spinner"] == "Dots"
    assert "启动 Hyperlight 沙箱" in "".join(raw), "Actual Status preparation not rendered"
    proc.terminate(force=True)
    print(json.dumps({"ok": True, "wheel_burst_ms": wheel_ms, "drag_burst_ms": drag_ms, "checks": ["80 wheel packets without backlog", "output arrow keys scroll", "120 drag events keep latest coordinates and right copy", "message block selection excludes sidebar and adjacent messages", "file suggestions wheel and click", "native text right paste", "right copies selected input", "registered PNG clipboard right paste", "Ctrl+V DIB image paste", "empty bracketed image paste", "three real HTTP images", "actual MultiSelectionPrompt with two answers", "actual Status during VM preparation", "spinner choice persisted"], "artifacts": str(output)}))
finally:
    (output / "requests.json").write_text(json.dumps(requests, indent=2), encoding="utf-8")
    (output / "errors.json").write_text(json.dumps(errors), encoding="utf-8")
    (output / "terminal.raw.txt").write_text("".join(raw), encoding="utf-8")
    if proc.isalive(): proc.terminate(force=True)
    replace_clipboard(original); u.DestroyWindow(owner); server.shutdown()
