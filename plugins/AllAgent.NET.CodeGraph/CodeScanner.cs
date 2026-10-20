using TreeSitter;

namespace AllAgent.NET.CodeGraph;

internal sealed record LanguageSpec(
    string Grammar,
    IReadOnlyDictionary<string, string> Definitions,
    IReadOnlyDictionary<string, string> References);

internal static class CodeScanner
{
    private static readonly LanguageSpec CSharp = new(
        "C#",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["namespace_declaration"] = "namespace",
            ["file_scoped_namespace_declaration"] = "namespace",
            ["class_declaration"] = "class",
            ["struct_declaration"] = "struct",
            ["interface_declaration"] = "interface",
            ["record_declaration"] = "record",
            ["enum_declaration"] = "enum",
            ["delegate_declaration"] = "delegate",
            ["method_declaration"] = "method",
            ["constructor_declaration"] = "constructor",
            ["destructor_declaration"] = "destructor",
            ["property_declaration"] = "property",
            ["indexer_declaration"] = "indexer",
            ["event_declaration"] = "event",
            ["event_field_declaration"] = "event",
            ["field_declaration"] = "field",
            ["enum_member_declaration"] = "enum-member",
            ["local_function_statement"] = "method",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["invocation_expression"] = "function",
            ["object_creation_expression"] = "type",
            ["member_access_expression"] = "name",
        });

    private static readonly LanguageSpec TypeScript = new(
        "TypeScript",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["class_declaration"] = "class",
            ["interface_declaration"] = "interface",
            ["type_alias_declaration"] = "type",
            ["enum_declaration"] = "enum",
            ["function_declaration"] = "function",
            ["generator_function_declaration"] = "function",
            ["function_signature"] = "function",
            ["method_definition"] = "method",
            ["method_signature"] = "method",
            ["public_field_definition"] = "field",
            ["lexical_declaration"] = "variable",
            ["variable_declaration"] = "variable",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["call_expression"] = "function",
            ["new_expression"] = "constructor",
            ["member_expression"] = "property",
        });

    private static readonly LanguageSpec Tsx = TypeScript with { Grammar = "TSX" };
    private static readonly LanguageSpec JavaScript = TypeScript with { Grammar = "JavaScript" };

    private static readonly LanguageSpec Python = new(
        "Python",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["class_definition"] = "class",
            ["function_definition"] = "function",
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["call"] = "function",
            ["attribute"] = "attribute",
        });

    public static CodeIndexFile Scan(string relativePath, string language, string text, long modifiedTicks, long size)
    {
        var file = new CodeIndexFile
        {
            Path = relativePath,
            Language = language,
            ModifiedTicks = modifiedTicks,
            Size = size,
        };
        var spec = Spec(language);
        if (spec == null || !TreeSitterNative.IsGrammarAvailable(spec.Grammar)) return file;
        try
        {
            using var grammar = new Language(spec.Grammar);
            using var parser = new Parser(grammar);
            using var tree = parser.Parse(text);
            if (tree == null) return file;
            var seen = new HashSet<(string Name, int Line)>();
            Walk(tree.RootNode, file, text, spec, seen);
            file.Symbols.Sort((left, right) => left.StartLine.CompareTo(right.StartLine));
            file.References.Sort((left, right) => left.Line.CompareTo(right.Line));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            file.Error = ex.Message;
        }
        return file;
    }

    private static LanguageSpec? Spec(string language) => language switch
    {
        "csharp" => CSharp,
        "typescript" => TypeScript,
        "tsx" => Tsx,
        "javascript" => JavaScript,
        "python" => Python,
        _ => null,
    };

    private static void Walk(Node root, CodeIndexFile file, string text, LanguageSpec spec, HashSet<(string, int)> seen)
    {
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (spec.Definitions.TryGetValue(node.Type, out var kind)) AddDefinition(node, kind, file, text, spec);
            if (spec.References.TryGetValue(node.Type, out var field)) AddReference(node, field, file, seen);
            foreach (var child in node.NamedChildren) pending.Push(child);
        }
    }

    private static void AddDefinition(Node node, string kind, CodeIndexFile file, string text, LanguageSpec spec)
    {
        if (node.Type is "lexical_declaration" or "variable_declaration")
        {
            foreach (var declarator in node.NamedChildren.Where(child => child.Type == "variable_declarator"))
            {
                var isFunction = declarator.GetChildForField("value")?.Type is "arrow_function" or "function_expression" or "function";
                AddSymbol(declarator, isFunction ? "function" : kind, file, text, spec);
            }
            return;
        }
        if (node.Type is "field_declaration" or "event_field_declaration")
        {
            var fieldKind = node.Type == "event_field_declaration" ? "event" : "field";
            foreach (var declarator in Descendants(node, "variable_declarator")) AddSymbol(declarator, fieldKind, file, text, spec);
            return;
        }
        AddSymbol(node, kind, file, text, spec);
    }

    private static void AddSymbol(Node node, string kind, CodeIndexFile file, string text, LanguageSpec spec)
    {
        var name = ExtractName(node, spec, kind);
        if (string.IsNullOrEmpty(name)) return;
        var container = ContainerOf(node, spec);
        var startLine = node.StartPosition.Row + 1;
        var endLine = Math.Max(startLine, node.EndPosition.Row + 1);
        file.Symbols.Add(new CodeSymbol
        {
            Name = name,
            FullName = string.IsNullOrEmpty(container) ? name : container + "." + name,
            Kind = kind,
            File = file.Path,
            StartLine = startLine,
            EndLine = endLine,
            Container = container,
            Signature = Signature(text, node),
        });
    }

    private static string? ExtractName(Node node, LanguageSpec spec, string kind)
    {
        var nameNode = node.GetChildForField("name");
        if (nameNode != null)
        {
            if (kind == "namespace")
            {
                var value = string.Concat(nameNode.Text.Where(character => !char.IsWhiteSpace(character)));
                return value.Length > 0 ? value : null;
            }
            return LastIdentifier(nameNode);
        }
        foreach (var child in node.NamedChildren)
        {
            if (child.Type is not ("identifier" or "property_identifier" or "field_identifier" or "type_identifier")) continue;
            if (LastIdentifier(child) is { } candidate) return candidate;
        }
        return null;
    }

    private static string? ContainerOf(Node node, LanguageSpec spec)
    {
        List<string>? parts = null;
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        {
            if (!spec.Definitions.TryGetValue(parent.Type, out var kind)) continue;
            if (ExtractName(parent, spec, kind) is not { Length: > 0 } name) continue;
            parts ??= [];
            parts.Insert(0, name);
        }
        return parts == null ? null : string.Join('.', parts);
    }

    private static void AddReference(Node node, string field, CodeIndexFile file, HashSet<(string, int)> seen)
    {
        var target = node.GetChildForField(field);
        if (LastIdentifier(target) is not { } name) return;
        var line = node.StartPosition.Row + 1;
        if (!seen.Add((name, line))) return;
        file.References.Add(new CodeReference { Name = name, File = file.Path, Line = line });
    }

    private static string? LastIdentifier(Node? node)
    {
        if (node == null) return null;
        switch (node.Type)
        {
            case "identifier":
            case "property_identifier":
            case "field_identifier":
            case "type_identifier":
                return Clean(node.Text);
            case "generic_name":
                return node.NamedChildren.Select(LastIdentifier).FirstOrDefault(name => name != null);
            case "qualified_name":
                return node.GetChildForField("name") is { } qualified
                    ? LastIdentifier(qualified)
                    : node.NamedChildren.Select(LastIdentifier).LastOrDefault(name => name != null);
            case "member_access_expression":
                return LastIdentifier(node.GetChildForField("name"));
            case "member_expression":
                return LastIdentifier(node.GetChildForField("property"));
            case "attribute":
                return LastIdentifier(node.GetChildForField("attribute"));
            case "scoped_identifier":
                return LastIdentifier(node.GetChildForField("name"));
            default:
                return null;
        }
    }

    private static string? Clean(string text)
    {
        var value = text.Trim().TrimStart('@', '#');
        if (value.Length is 0 or > 128) return null;
        if (!(char.IsLetter(value[0]) || value[0] is '_' or '$')) return null;
        foreach (var character in value)
            if (!(char.IsLetterOrDigit(character) || character is '_' or '$')) return null;
        return value;
    }

    private static string? Signature(string text, Node node)
    {
        var start = node.StartIndex;
        if (start < 0 || start >= text.Length) return null;
        var limit = Math.Min(text.Length, start + 400);
        var end = limit;
        for (var index = start; index < limit; index++)
        {
            var character = text[index];
            if (character is '{' or '\n' or '\r') { end = index; break; }
            if (character == '=' && index + 1 < limit && text[index + 1] == '>') { end = index; break; }
        }
        var signature = string.Join(' ', text[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return signature.Length switch
        {
            0 => null,
            > 200 => signature[..200] + "…",
            _ => signature,
        };
    }

    private static IEnumerable<Node> Descendants(Node node, string type)
    {
        var pending = new Stack<Node>();
        foreach (var child in node.NamedChildren) pending.Push(child);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Type == type) yield return current;
            foreach (var child in current.NamedChildren) pending.Push(child);
        }
    }
}
