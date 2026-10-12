"""Trusted parser only. Model code is never executed in the host interpreter."""
import ast
import json
import sys

SAFE_NODES = {ast.Module, ast.Expr, ast.Assign, ast.AugAssign, ast.Name, ast.Load, ast.Store, ast.Constant, ast.List, ast.Tuple, ast.Dict, ast.Set, ast.Subscript, ast.Slice, ast.BinOp, ast.UnaryOp, ast.BoolOp, ast.Compare, ast.If, ast.IfExp, ast.For, ast.While, ast.Break, ast.Continue, ast.Pass, ast.Call, ast.keyword, ast.Attribute, ast.ListComp, ast.SetComp, ast.DictComp, ast.GeneratorExp, ast.comprehension, ast.JoinedStr, ast.FormattedValue, ast.FunctionDef, ast.arguments, ast.arg, ast.Return}

def validate(code, policy):
    # Policy is supplied only by the trusted C# host, never by model arguments.
    SAFE_CALLS = set(policy["functions"])
    SAFE_METHODS = set(policy["methods"])
    if not isinstance(code, str) or not code.strip() or len(code) > 32000:
        raise ValueError("Code must contain 1–32000 characters")
    tree = ast.parse(code)
    functions = {node.name for node in ast.walk(tree) if isinstance(node, ast.FunctionDef)}
    for node in ast.walk(tree):
        if type(node) not in SAFE_NODES and not isinstance(node, (ast.operator, ast.unaryop, ast.boolop, ast.cmpop)):
            raise ValueError("Forbidden syntax: " + type(node).__name__)
        if isinstance(node, ast.FunctionDef):
            if node.name.startswith("_") or node.name in SAFE_CALLS:
                raise ValueError("Reserved name: " + node.name)
            if node.decorator_list:
                raise ValueError("Function decorators are forbidden")
        if isinstance(node, ast.arg) and (node.arg.startswith("_") or node.arg in SAFE_CALLS or node.arg in functions):
            raise ValueError("Reserved name: " + node.arg)
        if isinstance(node, ast.Name):
            if node.id.startswith("_") or (isinstance(node.ctx, ast.Store) and (node.id in SAFE_CALLS or node.id in functions)):
                raise ValueError("Reserved name: " + node.id)
        if isinstance(node, ast.Attribute):
            if node.attr not in SAFE_METHODS or not isinstance(node.ctx, ast.Load):
                raise ValueError("Forbidden attribute: " + node.attr)
        if isinstance(node, ast.Call):
            if isinstance(node.func, ast.Name):
                if node.func.id not in SAFE_CALLS and node.func.id not in functions:
                    raise ValueError("Forbidden function: " + node.func.id)
                if node.func.id == "call_tool" and (len(node.args) != 1 or not isinstance(node.args[0], ast.Constant) or not isinstance(node.args[0].value, str)):
                    raise ValueError("call_tool requires one literal tool name and keyword arguments")
            elif not isinstance(node.func, ast.Attribute) or node.func.attr not in SAFE_METHODS:
                raise ValueError("Only call_tool and pure logic functions are allowed")
            if any(kw.arg is None for kw in node.keywords) or any(isinstance(arg, ast.Starred) for arg in node.args):
                raise ValueError("Argument unpacking is forbidden")
    return {"ok": True}

if __name__ == "__main__":
    try:
        request = json.loads(sys.stdin.read(200000))
        result = validate(request["code"], request["policy"])
    except (ValueError, SyntaxError, TypeError, KeyError, RecursionError) as error:
        result = {"ok": False, "error": str(error)[:1000]}
    print(json.dumps(result))
