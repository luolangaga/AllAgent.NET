using System.Text.Json;
using System.Text.Json.Nodes;

if (args.FirstOrDefault() == "mcp")
{
    string? line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        var request = JsonNode.Parse(line)!;
        if (request["id"] == null) continue;
        var method = request["method"]?.GetValue<string>();
        object result = method switch
        {
            "initialize" => new { protocolVersion = request["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26", serverInfo = new { name = "LanAgent test probe", version = "1" }, capabilities = new { tools = new { }, prompts = new { }, resources = new { } } },
            "tools/list" => new { tools = new[] { new { name = "echo", description = "Test data routing", inputSchema = new { type = "object", properties = new { payload = new { type = "object" } } } } } },
            "tools/call" => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { payload = request["params"]?["arguments"], startupArgs = args.Skip(1).ToArray(), label = Environment.GetEnvironmentVariable("MCP_TEST_LABEL") }) } }, isError = false },
            "prompts/list" => new { prompts = new[] { new { name = "review", description = "Review test target", arguments = new[] { new { name = "target", required = true } } } } },
            "prompts/get" => new { description = "Test prompt", messages = new[] { new { role = "user", content = new { type = "text", text = "Review " + request["params"]?["arguments"]?["target"]?.GetValue<string>() } } } },
            "resources/list" => new { resources = new[] { new { uri = "test://proof", name = "proof", mimeType = "text/plain" } } },
            "resources/read" => new { contents = new[] { new { uri = "test://proof", mimeType = "text/plain", text = new string('r', 50000) } } },
            _ => new { }
        };
        Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request["id"], result }));
        await Console.Out.FlushAsync();
    }
}
else if (args.FirstOrDefault() == "noisy")
{
    for (var i = 0; i < 1000; i++) { Console.Out.Write(new string('o', 1000)); Console.Error.Write(new string('e', 1000)); }
}
else if (args.FirstOrDefault() == "wait") await Task.Delay(TimeSpan.FromMinutes(5));
else Console.WriteLine(JsonSerializer.Serialize(args));
