"""Local-only, credential-free TUI acceptance fixture. Never contacts a model provider."""
import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=18761)
parser.add_argument("--background", action="store_true")
parser.add_argument("--codeact", action="store_true", help="Exercise multiline reasoning and recoverable C# tool errors")
parser.add_argument("--max-requests", type=int, default=0, help="Stop automatically after this many fixture responses")
parser.add_argument("--experience", action="store_true", help="Run real installed npm and node via one CodeAct program")
parser.add_argument("--continuation", action="store_true", help="After host operations, truncate a response containing a valid but uncommitted file write")
parser.add_argument("--external", help="Exercise conversation-bubble directory approval for this fixture directory")
options = parser.parse_args()


class Handler(BaseHTTPRequestHandler):
    turn = 0

    def log_message(self, *args):
        pass

    def do_POST(self):
        try:
            self.stream_response()
        finally:
            if options.max_requests > 0 and Handler.turn >= options.max_requests:
                threading.Thread(target=self.server.shutdown, daemon=True).start()

    def stream_response(self):
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        assert [tool["function"]["name"] for tool in request["tools"]] == ["execute_code"]
        Handler.turn += 1
        turn = Handler.turn
        print("Fixture request", Handler.turn, flush=True)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()

        def delta(value, finish=None):
            frame = {"choices": [{"index": 0, "delta": value, "finish_reason": finish}]}
            self.wfile.write(("data: " + json.dumps(frame) + "\n\n").encode())
            self.wfile.flush()

        if options.codeact:
            for line in ["先验证工具错误是结构化结果。\n", "再使用工作区相对路径检查目录。\n", "这是第三行思考，终端应该在同一行滚动展示，并保留逻辑行数 " * 4]:
                delta({"reasoning_content": line})
                time.sleep(1)
        else:
            delta({"reasoning_content": "正在检查任务需求，接下来确认你的选择，然后创建页面。"})
        time.sleep(2)
        background = options.background and "[background-qa]" in next(message["content"] for message in reversed(request["messages"]) if message["role"] == "user")
        completed = sum(message["role"] == "tool" for message in request["messages"])
        if options.background and not background:
            delta({"content": "\n".join("滚动验收第 %03d 行：前台聊天与后台任务同时可用。" % number for number in range(100))})
            delta({}, "stop")
            self.wfile.write(b"data: [DONE]\n\n")
            return
        last_user = max(index for index, message in enumerate(request["messages"]) if message["role"] == "user")
        current_results = sum(message["role"] == "tool" for message in request["messages"][last_user + 1:])
        if options.external and turn == 1:
            code = "r = call_tool('list_directory', path=" + repr(options.external) + ")\nprint(r['ok'])\nr = call_tool('list_directory', path=" + repr(options.external) + ")\nprint(r['ok'])"
        elif options.external:
            delta({"content": "外部目录验收完成：同会话记住目录后再次读取无需重复询问。"})
            delta({}, "stop")
            self.wfile.write(b"data: [DONE]\n\n")
            return
        elif options.experience and current_results == 0:
            if options.continuation and turn >= 3:
                delta({"content": "输出截断后任务自动续接完成，已完成工具未重放。"})
                delta({}, "stop")
                self.wfile.write(b"data: [DONE]\n\n")
                return
            code = "checks = [('npm', ['--version']), ('node', ['--version'])]\nresults = []\nfor exe, argv in checks:\n    r = call_tool('run_shell', exe=exe, args=argv, timeout_seconds=10)\n    results.append({'exe': exe, 'ok': r['ok'], 'data': r['data'], 'error': r['error']})\nprint(results)"
        elif options.experience:
            if options.continuation and turn == 2:
                delta({"content": "继续创建文件时输出触达上限。"})
                code = "r = call_tool('file_write', path='must-not-exist.txt', content='truncated response must not execute')"
                delta({"tool_calls": [{"index": 0, "id": "discarded-prefix", "type": "function", "function": {"name": "execute_code", "arguments": json.dumps({"code": code})}}]})
                delta({}, "length")
                self.wfile.write(b"data: [DONE]\n\n")
                return
            delta({"content": "npm 和 node 的版本检查已完成；实际 Python 源码和逐次工具结果可在执行详情中查看。"})
            delta({}, "stop")
            self.wfile.write(b"data: [DONE]\n\n")
            return
        elif options.codeact and turn == 1:
            code = "r = call_tool('file_read', path='missing-for-codeact-qa.txt')\nprint(r['ok'])\nr = call_tool('list_directory', path='.')\nprint(r['ok'])"
        elif options.codeact:
            delta({"content": "工具失败已返回结构化结果；同一次 Python 执行继续完成目录检查。"})
            delta({}, "stop")
            self.wfile.write(b"data: [DONE]\n\n")
            return
        elif (background and completed == 0) or (not options.background and turn == 1):
            code = "r = call_tool('ask_user', question='小鸟骑车页面使用哪种主题？', choices=['深色', '浅色'])\nprint(r)"
        elif (background and completed == 1) or (not options.background and turn == 2):
            content = "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head><meta charset=\"UTF-8\"><title>小鸟骑自行车</title></head>\n<body>\n<h1>小鸟骑自行车 🐦</h1>\n<p>这是终端授权验收生成的测试页面。</p>\n</body>\n</html>\n"
            code = "r = call_tool('file_write', path='bird.html', content=" + repr(content) + ", overwrite=True)\nprint(r)"
        else:
            delta({"content": "已创建 bird.html，中文内容保存正确。你可以通过 /diff 检查变更，或 /undo 撤销这一轮。"})
            delta({}, "stop")
            self.wfile.write(b"data: [DONE]\n\n")
            return
        arguments = json.dumps({"code": code})
        delta({"tool_calls": [{"index": 0, "id": "fixture-" + str(Handler.turn), "type": "function", "function": {"name": "execute_code", "arguments": arguments}}]})
        delta({}, "tool_calls")
        self.wfile.write(b"data: [DONE]\n\n")


print("Local TUI fixture ready on port", options.port, flush=True)
with ThreadingHTTPServer(("127.0.0.1", options.port), Handler) as server:
    server.serve_forever()
