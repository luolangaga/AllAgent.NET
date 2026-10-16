"""Trusted transport only. Model source is interpreted by Monty, never CPython."""
import json
import os
import sys
import sysconfig
from pydantic_monty import Monty, CollectString

sys.stdin.reconfigure(encoding='utf-8')
sys.stdout.reconfigure(encoding='utf-8')

def send(value):
    print(json.dumps(value, ensure_ascii=True), flush=True)

request = json.loads(sys.stdin.readline())
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
    send({'kind': 'call', 'name': tool_name, 'arguments': parameters})
    reply = json.loads(sys.stdin.readline())
    if reply.get('kind') != 'tool_result':
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
    send({'kind': 'result', 'result': {'ok': False, 'data': {'stdout': collector.output[:20000],
          'stderr': message, 'exitCode': 1}, 'error': 'Monty execution failed: ' + message,
          'truncated': len(collector.output) > 20000}})
