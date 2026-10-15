import sys, json, traceback, ast, importlib

pipe_name = sys.argv[1]
payload = json.loads(sys.stdin.read())
pipe = None

def call_tool(name, **arguments):
    global pipe
    if pipe is None:
        pipe = open(pipe_name, 'r+b', buffering=0)
    request = (json.dumps({'name': name, 'arguments': arguments}, ensure_ascii=False) + '\n').encode('utf-8')
    pipe.write(request)
    response = bytearray()
    while True:
        character = pipe.read(1)
        if not character:
            raise RuntimeError('Host tool connection closed')
        if character == b'\n':
            return json.loads(response.decode('utf-8'))
        response.extend(character)

try:
    namespace = {'__name__': '__main__', 'call_tool': call_tool}
    used_names = {node.id for node in ast.walk(ast.parse(payload['code'])) if isinstance(node, ast.Name)}
    for alias, target in payload.get('aliases', {}).items():
        if alias not in used_names:
            continue
        module_name, separator, attribute = target.partition(':')
        module = importlib.import_module(module_name)
        namespace[alias] = getattr(module, attribute) if separator else module
    exec(compile(payload['code'], '<lanagent-full-access>', 'exec'), namespace, namespace)
except BaseException:
    traceback.print_exc()
    sys.exit(1)
finally:
    if pipe is not None:
        pipe.close()
