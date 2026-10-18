using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Browser composer input: `@` file references and pasted image attachments.</summary>
public sealed class WebPromptInputTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";

    [Fact]
    public void WorkspaceListingCompletesDirectoriesAndFindsFilesByName()
    {
        var root = TempTree();
        try
        {
            // The root listing offers directories and loose files together.
            var all = WorkspaceFiles.Match(root, "");
            Assert.Contains(all, entry => entry is { Path: "src", IsDirectory: true });
            Assert.Contains(all, entry => entry.Path == "README.md");

            // A trailing separator drills into that directory only.
            var inside = WorkspaceFiles.Match(root, "src/");
            Assert.Contains(inside, entry => entry.Path == "src/App.tsx");
            Assert.Contains(inside, entry => entry is { Path: "src/views", IsDirectory: true });
            Assert.DoesNotContain(inside, entry => entry.Path == "README.md");

            // A bare name searches the tree, skipping build output and VCS metadata.
            var byName = WorkspaceFiles.Match(root, "Chat.tsx");
            Assert.Equal("src/views/Chat.tsx", Assert.Single(byName).Path);

            // Partial names inside a directory complete against that directory.
            Assert.Equal("src/App.tsx", Assert.Single(WorkspaceFiles.Match(root, "src/App")).Path);
            Assert.Empty(WorkspaceFiles.Match(root, "../outside"));
            Assert.Empty(WorkspaceFiles.Match(root, "src/nope/"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BrowserTurnCarriesPastedImagesAndResolvesFileReferences()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "notes.txt"), "REFERENCED-BODY");
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var config = new ApiConfig { ProviderId = "web-fixture", DefaultModel = "fixture" };
        store.AddApiConfig(config);
        var provider = new AnswerProvider();
        await using var service = new AgentService(store, new AiProviderFactory([provider]), workspace.Options with { CodeTimeout = TimeSpan.FromSeconds(60) });
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var dispatcher = new BridgeDispatcher();
        new BridgeHandlerRegistrar(dispatcher, store, service, new AiProviderFactory([provider]), new BridgeEventPusher(), mcp).RegisterAll();

        var image = ImageAttachment.FromBytes("pasted.png", Convert.FromBase64String(Png));
        var request = JsonSerializer.Serialize(new
        {
            id = "send",
            method = "chat.startStream",
            @params = new { conversationId = conversation.Id, message = "看下这张截图和 @notes.txt", apiConfigId = config.Id, images = new[] { image } },
        }, JsonOptions.Default);
        using var response = JsonDocument.Parse(await dispatcher.InvokeAsync(request));
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("error").ValueKind);
        var streamId = response.RootElement.GetProperty("data").GetString();
        Assert.NotNull(streamId);

        var user = await WaitForUserMessage(store, conversation.Id);
        await service.CancelStreamAsync(streamId!);
        Assert.Equal(Png, Assert.Single(user.Images).Base64);
        Assert.Contains("REFERENCED-BODY", user.Content);
        Assert.Equal("看下这张截图和 @notes.txt", user.InputText);

        // History replay hands the browser the same attachments and the human draft.
        using var history = JsonDocument.Parse(await dispatcher.InvokeAsync(JsonSerializer.Serialize(new
        {
            id = "history", method = "conversations.getMessages", @params = new { conversationId = conversation.Id },
        }, JsonOptions.Default)));
        var replayed = history.RootElement.GetProperty("data")[0];
        Assert.Equal("看下这张截图和 @notes.txt", replayed.GetProperty("content").GetString());
        Assert.Equal(Png, replayed.GetProperty("images")[0].GetProperty("base64").GetString());
    }

    [Fact]
    public async Task BrowserSendRejectsUnusableAttachments()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var config = new ApiConfig { ProviderId = "web-fixture", DefaultModel = "fixture" };
        store.AddApiConfig(config);
        var factory = new AiProviderFactory();
        await using var service = new AgentService(store, factory, workspace.Options);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var dispatcher = new BridgeDispatcher();
        new BridgeHandlerRegistrar(dispatcher, store, service, factory, new BridgeEventPusher(), mcp).RegisterAll();

        async Task<string> Error(object parameters)
        {
            using var response = JsonDocument.Parse(await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "send", method = "chat.startStream", @params = parameters }, JsonOptions.Default)));
            var error = response.RootElement.GetProperty("error");
            Assert.Equal(JsonValueKind.String, error.ValueKind);
            return error.GetString()!;
        }

        var valid = ImageAttachment.FromBytes("pixel.png", Convert.FromBase64String(Png));
        Assert.Contains("最多 8 张", await Error(new { conversationId = conversation.Id, message = "看图", apiConfigId = config.Id, images = Enumerable.Repeat(valid, 9).ToArray() }));
        Assert.Contains("不支持的图片格式", await Error(new { conversationId = conversation.Id, message = "看图", apiConfigId = config.Id, images = new[] { new ImageAttachment("fake.png", "image/png", Convert.ToBase64String(new byte[20])) } }));
        Assert.False(service.IsRunning(conversation.Id));
    }

    [Fact]
    public async Task ReferencesTheWorkspaceCannotResolveStayAsPlainText()
    {
        // Typed prose like `@nuxt/ui` must not sink the whole turn; the terminal keeps its
        // fail-fast behaviour, the browser composer sends the message unchanged.
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        var config = new ApiConfig { ProviderId = "web-fixture", DefaultModel = "fixture" };
        store.AddApiConfig(config);
        var provider = new AnswerProvider();
        await using var service = new AgentService(store, new AiProviderFactory([provider]), workspace.Options with { CodeTimeout = TimeSpan.FromSeconds(60) });
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var dispatcher = new BridgeDispatcher();
        new BridgeHandlerRegistrar(dispatcher, store, service, new AiProviderFactory([provider]), new BridgeEventPusher(), mcp).RegisterAll();

        using var response = JsonDocument.Parse(await dispatcher.InvokeAsync(JsonSerializer.Serialize(new
        {
            id = "send",
            method = "chat.startStream",
            @params = new { conversationId = conversation.Id, message = "参考 @nuxt/ui 的做法，还有 @../outside", apiConfigId = config.Id },
        }, JsonOptions.Default)));
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("error").ValueKind);
        var streamId = response.RootElement.GetProperty("data").GetString();

        var user = await WaitForUserMessage(store, conversation.Id);
        await service.CancelStreamAsync(streamId!);
        Assert.Equal("参考 @nuxt/ui 的做法，还有 @../outside", user.Content);
        Assert.Empty(user.Images);
        Assert.DoesNotContain("File references", user.Content);
    }

    private static string TempTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "allagent-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "views"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "junk"));
        File.WriteAllText(Path.Combine(root, "README.md"), "readme");
        File.WriteAllText(Path.Combine(root, "src", "App.tsx"), "app");
        File.WriteAllText(Path.Combine(root, "src", "views", "Chat.tsx"), "chat");
        File.WriteAllText(Path.Combine(root, "node_modules", "junk", "Chat.tsx"), "junk");
        return root;
    }

    private static async Task<Message> WaitForUserMessage(DataStore store, Guid conversationId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var message = store.GetMessages(conversationId).FirstOrDefault(item => item.Role == "user");
            if (message != null) return message;
            await Task.Delay(25);
        }
        throw new TimeoutException("The user turn was never persisted.");
    }

    private sealed class AnswerProvider : IAiProvider
    {
        public string ProviderId => "web-fixture";
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new(ChunkType.TextDelta, TextDelta: "收到");
            yield return new(ChunkType.Done, FinishReason: "stop");
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }
}
