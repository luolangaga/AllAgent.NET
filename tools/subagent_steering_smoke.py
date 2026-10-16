"""Real terminal host + local SSE + parent/child Hyperlight steering acceptance."""
import json
import os
import base64
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import subprocess
import threading
import time
import uuid

root = Path(__file__).resolve().parents[1]
executable = os.environ.get("LANAGENT_QA_EXECUTABLE", str(root / "src/LanAgent.Terminal/bin/Release/net10.0/lanagent.exe"))
output = root / "artifacts/qa/subagent-steering" / uuid.uuid4().hex
output.mkdir(parents=True)
workspace = output / "workspace"
workspace.mkdir()
(workspace / "safe.txt").write_text("READ-ONLY-PROOF", encoding="utf-8")
(workspace / ".env").write_text("SECRET-MUST-NOT-LEAK", encoding="utf-8")
(workspace / "ask.txt").write_text("ASK-MUST-NOT-LEAK", encoding="utf-8")
png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jI1kAAAAASUVORK5CYII="
image = workspace / "correction.png"
image.write_bytes(base64.b64decode(png))
reference = workspace / "evidence.txt"
reference.write_text("TEXT-SNAPSHOT-PROOF", encoding="utf-8")
correction_prompt = 'CORRECTION-CHILD-PROOF @"correction.png" @"evidence.txt"'
session, config, alternate = str(uuid.uuid4()), str(uuid.uuid4()), str(uuid.uuid4())
requests = []
errors = []
child_started = threading.Event()
parent_calls = 0
code = '\n'.join([
    'print(call_tool("subagent", agent="general", description="invalid attachment", prompt=\'INVALID-ATTACHMENT @"missing.png"\', background=True))',
    'job = call_tool("subagent", agent="general", description="steering acceptance", prompt="START-CHILD-PROOF", background=True)',
    'print(job)',
])
permission_code = '\n'.join([
    'print(call_tool("file_write", path="must-not-write.txt", content="blocked"))',
    'print(call_tool("file_read", path="safe.txt"))',
    'print(call_tool("file_read", path="sub/../.env"))',
    'print(call_tool("file_read", path="ask.txt"))',
])

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass
    def do_POST(self):
        global parent_calls
        try:
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            request["_qa_endpoint"] = self.server.server_port
            request["_qa_authorization"] = self.headers.get("Authorization")
            requests.append(request)
            def text(message):
                value = message.get("content")
                return value if isinstance(value, str) else "\n".join(p.get("text", "") for p in (value or []))
            user = [text(m) for m in request["messages"] if m["role"] == "user"]
            child = "START-CHILD-PROOF" in user
            corrected = any(t.startswith("CORRECTION-CHILD-PROOF") for t in user)
            resumed = "RESUMED-PERMISSION-PROOF" in user
            if not child:
                parent_calls += 1
            parent_code = code if not child and parent_calls == 1 else None
            if not child and parent_calls == 2:
                assert child_started.wait(35), "Child did not enter its actual response stream"
                saved = json.loads((output / "lanagent.json").read_text(encoding="utf-8-sig"))
                child_id = next(c["id"] for c in saved["conversations"] if c.get("parentConversationId") == session)
                parent_code = 'print(call_tool("subagent", agent="reviewer", description="correction", prompt=' + json.dumps(correction_prompt) + ', sessionID=' + json.dumps(child_id) + '))'
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            def event(value):
                self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode())
                self.wfile.flush()
            if child and not corrected:
                child_started.set()
                event({"choices": [{"delta": {"content": "CHILD-FIRST-RESPONSE"}}]})
                end = time.monotonic() + 40
                while time.monotonic() < end:
                    try:
                        saved = json.loads((output / "lanagent.json").read_text(encoding="utf-8-sig"))
                        if any(p["content"].startswith("CORRECTION-CHILD-PROOF") and p["delivery"] == "steer" for p in saved.get("pendingInputs", [])):
                            image.unlink()
                            reference.unlink()
                            break
                    except (OSError, ValueError):
                        pass
                    time.sleep(0.03)
                else:
                    raise AssertionError("Running child never received durable steering")
            elif parent_code:
                event({"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "delegate-proof-" + str(parent_calls), "type": "function", "function": {"name": "execute_code", "arguments": json.dumps({"code": parent_code})}}]}}]})
            elif child and corrected and (not any(m["role"] == "tool" for m in request["messages"]) or resumed and not any(m["role"] == "tool" for m in request["messages"][next(i for i, m in enumerate(request["messages"]) if text(m) == "RESUMED-PERMISSION-PROOF") + 1:])):
                parent_code = permission_code
                event({"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "role-guard-proof", "type": "function", "function": {"name": "execute_code", "arguments": json.dumps({"code": parent_code})}}]}}]})
            else:
                event({"choices": [{"delta": {"content": "CHILD-CORRECTED-FINAL" if child else "PARENT-DONE-PROOF"}}]})
            event({"choices": [{"delta": {}, "finish_reason": "tool_calls" if parent_code else "stop"}]})
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
        except Exception as exc:
            errors.append(str(exc))

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
second_server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
threading.Thread(target=second_server.serve_forever, daemon=True).start()
(output / "lanagent.json").write_text(json.dumps({"conversations": [{"id": session, "title": "Parent steering fixture", "messages": []}],
    "apiConfigs": [{"id": config, "providerId": "openai", "displayName": "fixture", "protocol": "openai", "apiKey": "first-fixture-key", "baseUrl": f"http://127.0.0.1:{server.server_port}", "defaultModel": "fixture", "models": ["fixture"], "isEnabled": True},
                   {"id": alternate, "providerId": "openai", "displayName": "alternate", "protocol": "openai", "apiKey": "second-fixture-key", "baseUrl": f"http://127.0.0.1:{second_server.server_port}", "defaultModel": "alternate-default", "models": ["switched-fixture"], "isEnabled": True}],
    "skills": [], "appSettings": {}, "mcpServerConfigs": []}), encoding="utf-8")
try:
    role_file = workspace / "agents.json"
    role_file.write_text(json.dumps([{"name": "reviewer", "description": "Read-only review specialist", "instructions": "CUSTOM-REVIEWER-PROOF: Review code and return evidence.", "readOnly": True, "model": "switched-fixture", "provider": alternate,
        "permissions": [{"action": "*", "resource": "*", "effect": "allow"}, {"action": "read", "resource": "*.env", "effect": "deny"}, {"action": "read", "resource": "*/ask.txt", "effect": "ask"}]}]), encoding="utf-8")
    imported = subprocess.run([executable, "agents", "import", str(role_file), "--workspace", str(workspace), "--data-dir", str(output), "--json"], capture_output=True, text=True, encoding="utf-8", timeout=15)
    assert imported.returncode == 0, imported.stdout + imported.stderr
    shown = subprocess.run([executable, "agents", "show", "reviewer", "--workspace", str(workspace), "--data-dir", str(output), "--json"], capture_output=True, text=True, encoding="utf-8", timeout=15)
    assert shown.returncode == 0 and "CUSTOM-REVIEWER-PROOF" in shown.stdout and "switched-fixture" in shown.stdout, shown.stdout + shown.stderr
    (output / "role-show.json").write_text(shown.stdout, encoding="utf-8")
    assert not requests, "Role management unexpectedly contacted a model"
    result = subprocess.run([executable, "run", "PARENT-STEER-PROOF", "--workspace", str(workspace), "--data-dir", str(output), "--resume", session, "--config", config, "--json", "--full-access"], cwd=root, capture_output=True, text=True, encoding="utf-8", timeout=100)
    (output / "events.jsonl").write_text(result.stdout, encoding="utf-8")
    (output / "stderr.txt").write_text(result.stderr, encoding="utf-8")
    assert result.returncode == 0, result.stdout + result.stderr
    events = [json.loads(line) for line in result.stdout.splitlines() if line.startswith("{")]
    rejected = [json.loads(e["data"]["resultJson"]) for e in events if e.get("type") == "ToolCallCompleted" and e["data"].get("toolName") == "subagent"]
    assert any(not r["ok"] and "missing.png" in (r.get("error") or "") for r in rejected), rejected
    assert not errors, errors
    assert child_started.is_set(), "Actual child request missing"
    data = json.loads((output / "lanagent.json").read_text(encoding="utf-8-sig"))
    children = [c for c in data["conversations"] if c.get("parentConversationId") == session]
    assert len(children) == 1, children
    assert children[0]["modelId"] == "switched-fixture" and children[0]["subAgentConfigId"] == alternate
    switched_requests = [r for r in requests if r["model"] == "switched-fixture"]
    assert len(switched_requests) == 2 and all(r["_qa_endpoint"] == second_server.server_port for r in switched_requests)
    assert all(r["_qa_authorization"] == "Bearer second-fixture-key" for r in switched_requests)
    assert children[0]["agentRole"] == "reviewer" and not (workspace / "must-not-write.txt").exists()
    assert "CUSTOM-REVIEWER-PROOF" in switched_requests[0]["messages"][1]["content"]
    assert all(r["_qa_endpoint"] == server.server_port and r["_qa_authorization"] == "Bearer first-fixture-key" for r in requests if r["model"] == "fixture")
    messages = children[0]["messages"]
    assert any(m["role"] == "tool" and "read-only" in m["content"] and "READ-ONLY-PROOF" in m["content"] for m in messages)
    assert any(m["role"] == "tool" and "permission rules deny" in m["content"] and "permission approval denied" in m["content"] for m in messages)
    assert not any("SECRET-MUST-NOT-LEAK" in m["content"] or "ASK-MUST-NOT-LEAK" in m["content"] for m in messages)
    corrections = [m for m in messages if m["role"] == "user" and m["content"].startswith("CORRECTION-CHILD-PROOF")]
    assert len(corrections) == 1, messages
    assert corrections[0]["inputText"] == correction_prompt and "TEXT-SNAPSHOT-PROOF" in corrections[0]["content"]
    assert corrections[0]["images"][0]["base64"] == png
    assert not image.exists() and not reference.exists()
    blocks = [part for request in requests for message in request["messages"] if message["role"] == "user" and isinstance(message.get("content"), list)
              for part in message["content"] if part.get("type") == "image_url"]
    assert blocks and all(b["image_url"]["url"] == "data:image/png;base64," + png for b in blocks), blocks
    assert any(m["content"] == "CHILD-CORRECTED-FINAL" for m in messages)
    assert not data.get("pendingInputs"), data.get("pendingInputs")
    jobs = json.loads((output / "background-tasks.json").read_text(encoding="utf-8-sig"))
    assert len(jobs) == 1 and jobs[0]["FinalOutput"] == "CHILD-CORRECTED-FINAL", jobs
    assert all([t["function"]["name"] for t in r["tools"]] == ["execute_code"] for r in requests)
    parent = next(c for c in data["conversations"] if c["id"] == session)
    assert parent["modelId"] == "fixture"
    assert not any(m["role"] == "user" and m["content"].startswith("CORRECTION-CHILD-PROOF") for m in parent["messages"])
    resumed_run = subprocess.run([executable, "run", "RESUMED-PERMISSION-PROOF", "--workspace", str(workspace), "--data-dir", str(output), "--resume", children[0]["id"], "--config", alternate, "--model", "switched-fixture", "--json", "--full-access"], cwd=root, capture_output=True, text=True, encoding="utf-8", timeout=60)
    (output / "resume-events.jsonl").write_text(resumed_run.stdout, encoding="utf-8")
    assert resumed_run.returncode == 0, resumed_run.stdout + resumed_run.stderr
    resumed_data = json.loads((output / "lanagent.json").read_text(encoding="utf-8-sig"))
    resumed_messages = next(c for c in resumed_data["conversations"] if c["id"] == children[0]["id"])["messages"]
    boundary = next(i for i, m in enumerate(resumed_messages) if m["content"] == "RESUMED-PERMISSION-PROOF")
    assert any(m["role"] == "tool" and "permission rules deny" in m["content"] and "permission approval denied" in m["content"] and "READ-ONLY-PROOF" in m["content"] for m in resumed_messages[boundary+1:])
    assert not any("SECRET-MUST-NOT-LEAK" in m["content"] or "ASK-MUST-NOT-LEAK" in m["content"] for m in resumed_messages)
    assert not errors and not (workspace / "must-not-write.txt").exists()
    print(json.dumps({"ok": True, "checks": ["actual parent/child microVMs", "typed subagent steering during child stream", "custom role imported and persisted", "role default model/provider applied", "live read-only restrictions override full access", "deny and ask rules enforced under full access", "manual child resume retains rules", "workspace read retained", "custom role instructions", "runtime model/configuration/credential switch", "parent configuration retained", "same task and session", "correction admitted once", "image and text snapshots survive source deletion", "real HTTP image bytes", "foreground final result", "only execute_code exposed"], "artifacts": str(output)}))
finally:
    (output / "requests.json").write_text(json.dumps(requests, indent=2), encoding="utf-8")
    (output / "errors.json").write_text(json.dumps(errors), encoding="utf-8")
    server.shutdown()
    second_server.shutdown()
