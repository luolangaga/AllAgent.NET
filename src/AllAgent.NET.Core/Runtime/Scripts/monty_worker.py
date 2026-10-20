"""Trusted transport only. Model source is interpreted by Monty, never CPython."""
import json
import os
import sys
import sysconfig
import uuid
from pydantic_monty import Monty, CollectString

sys.stdin.reconfigure(encoding='utf-8')
sys.stdout.reconfigure(encoding='utf-8')

MAX_FRAME = 16 * 1024 * 1024
def read():
    line = sys.stdin.readline(MAX_FRAME + 1)
    if not line.endswith('\n') or len(line.encode('utf-8')) > MAX_FRAME:
        raise RuntimeError('Invalid or oversized sandbox frame')
    return json.loads(line)

def send(value):
    value.update(version=1, executionId=request['executionId'])
    text = json.dumps(value, ensure_ascii=True)
    if len(text.encode('utf-8')) > MAX_FRAME:
        raise RuntimeError('Sandbox frame exceeds 16 MiB')
    print(text, flush=True)

request = read()
if request.get('version') != 1 or not request.get('executionId'):
    raise RuntimeError('Invalid sandbox protocol version or execution ID')
allowed = set(request['tools'])

def call_tool(tool_name, /, arguments=None, **parameters):
    if arguments is not None:
        if not isinstance(arguments, dict):
            raise TypeError('call_tool second argument must be a dict')
        if any(key in parameters for key in arguments):
            raise TypeError('Duplicate tool argument')
        parameters = {**arguments, **parameters}
    if tool_name not in allowed:
        return {'ok': False, 'data': None, 'error': 'Unknown or unavailable host tool', 'truncated': False}
    call_id = uuid.uuid4().hex
    send({'kind': 'call', 'name': tool_name, 'arguments': parameters, 'callId': call_id})
    reply = read()
    if reply.get('kind') != 'tool_result' or reply.get('version') != 1 or reply.get('executionId') != request['executionId'] or reply.get('callId') != call_id:
        raise RuntimeError('Invalid host response')
    return reply['result']

collector = CollectString(max_bytes=80000)
try:
    binary = os.path.join(sysconfig.get_path('scripts'), 'monty.exe' if os.name == 'nt' else 'monty')
    with Monty(binary_path=binary, min_processes=1, max_processes=1,
               request_timeout=request['timeout']) as pool:
        with pool.checkout(limits={'max_memory': 128 * 1024 * 1024,
                                   'max_feed_duration_secs': request['timeout']}) as session:
            session.feed_run(request['code'], external_lookup={'call_tool': call_tool},
                             print_callback=collector)
    send({'kind': 'result', 'result': {'ok': True, 'data': {'stdout': collector.output[:20000],
          'stderr': '', 'exitCode': 0}, 'error': None, 'truncated': len(collector.output) > 20000}})
except Exception as error:
    message = str(error)[:4000]
    hint = ''
    if 'ModuleNotFoundError' in message or 'ImportError' in message:
        hint = ' The active Monty sandbox provides only the modules listed in its runtime guidance; use call_tool(...) for host I/O instead of importing another module.'
    send({'kind': 'result', 'result': {'ok': False, 'data': {'stdout': collector.output[:20000],
          'stderr': message, 'exitCode': 1}, 'error': 'Monty execution failed: ' + message + hint,
          'truncated': len(collector.output) > 20000}})
