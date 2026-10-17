using System.Text.Json;

/// <summary>Load bundled CPython source inside the guest, without filesystem mounts or host execution.</summary>
internal static class GuestPythonLibraries
{
    public static string Prelude { get; } = BuildPrelude();

    private static string BuildPrelude()
    {
        var modules = new[] { "struct", "base64", "encodings.utf_16_le", "encodings.utf_16_be", "encodings.utf_16" }
            .Select(name => new { name, source = ReadSource(name) });
        // Dependencies precede consumers. sys.modules is restored with the clean VM
        // snapshot; reinstall trusted sources before each user program is compiled.
        return "import json as _lan_json\n_lan_libraries = _lan_json.loads(" +
            JsonSerializer.Serialize(JsonSerializer.Serialize(modules)) + ")\n" + """
            import sys as _lan_sys
            import types as _lan_types
            import encodings as _lan_encodings
            for _lan_library in _lan_libraries:
                _lan_name = _lan_library['name']
                try:
                    __import__(_lan_name)
                    continue
                except ModuleNotFoundError as _lan_import_error:
                    if _lan_import_error.name != _lan_name:
                        raise
                _lan_module = _lan_types.ModuleType(_lan_name)
                _lan_module.__file__ = '<allagent-stdlib>/' + _lan_name.replace('.', '/') + '.py'
                _lan_module.__package__ = _lan_name.rpartition('.')[0]
                _lan_sys.modules[_lan_name] = _lan_module
                try:
                    exec(compile(_lan_library['source'], _lan_module.__file__, 'exec'), _lan_module.__dict__)
                except BaseException:
                    del _lan_sys.modules[_lan_name]
                    raise
                if _lan_module.__package__:
                    setattr(_lan_sys.modules[_lan_module.__package__], _lan_name.rpartition('.')[2], _lan_module)
            del _lan_libraries, _lan_library, _lan_name
            """ + "\n";
    }

    private static string ReadSource(string name)
    {
        using var stream = typeof(GuestPythonLibraries).Assembly.GetManifestResourceStream(
            "AllAgent.NET.Sandbox.Python.stdlib." + name + ".py")
            ?? throw new InvalidOperationException("Missing bundled Python library: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
