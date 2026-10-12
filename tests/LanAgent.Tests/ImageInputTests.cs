using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using LanAgent.Terminal;
using Microsoft.AspNetCore.Http;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class ImageInputTests
{
    [Fact]
    public void AttachmentPanelShowsNamesAndTypesWithoutBase64()
    {
        var image = ImageAttachment.FromBytes("截图.png", Convert.FromBase64String(Png));
        var console = new TestConsole(); console.Profile.Width = 45; console.Write(ImageAttachmentsView.Render([image]));
        Assert.Contains("截图.png", console.Output); Assert.Contains("image/png", console.Output); Assert.Contains("图片附件", console.Output);
        Assert.DoesNotContain(Png, console.Output);
    }
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jI1kAAAAASUVORK5CYII=";
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualCompactionSendsImageAndArchivesOnlyOnCompleteResponse(bool complete)
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var session = new Conversation(); store.AddConversation(session);
        var image = ImageAttachment.FromBytes("diagram.png", Convert.FromBase64String(Png));
        store.AddMessage(session.Id, new Message { Role = "user", ConversationId = session.Id, Content = "Explain diagram", Images = [image] });
        JsonElement captured = default;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body); captured = body.RootElement.Clone();
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(complete
                ? "data: {\"choices\":[{\"delta\":{\"content\":\"Visual finding from diagram.png\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "data: {\"choices\":[{\"delta\":{\"content\":\"Incomplete finding\"}}]}\n\ndata: [DONE]\n\n");
        });
        var config = new ApiConfig { ProviderId = "openai", BaseUrl = server.Url, DefaultModel = "fixture" };
        if (complete)
        {
            await SessionOperations.CompactAsync(store, session, config, new TerminalUi(true, false), 32000, "", default);
            var archive = Assert.Single(store.Conversations, c => c.Id != session.Id);
            Assert.Equal(Png, Assert.Single(Assert.Single(archive.Messages).Images).Base64);
            Assert.Contains(session.Messages, m => m.Content.Contains("Visual finding"));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SessionOperations.CompactAsync(store, session, config, new TerminalUi(true, false), 32000, "", default));
            Assert.Single(store.Conversations); Assert.Equal(Png, Assert.Single(Assert.Single(session.Messages).Images).Base64);
        }
        Assert.Equal(image.DataUrl, captured.GetProperty("messages")[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
    }
    [Theory]
    [InlineData("openai")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task RealHttpRequestsContainImageBytesInProtocolSpecificBlocks(string protocol)
    {
        JsonElement captured = default;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body); captured = body.RootElement.Clone();
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(protocol switch
            {
                "anthropic" => "event: message_delta\ndata: {\"delta\":{\"stop_reason\":\"end_turn\"}}\n\nevent: message_stop\ndata: {}\n\n",
                "gemini" => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                "responses" => "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n",
                _ => "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
            });
        });
        var config = new ApiConfig { ProviderId = protocol, Protocol = protocol, BaseUrl = server.Url, DefaultModel = "fixture", ApiKey = "fixture" };
        var image = ImageAttachment.FromBytes("pixel.png", Convert.FromBase64String(Png));
        IAiProvider provider = protocol switch { "responses" => new ResponsesProvider(), "anthropic" => new AnthropicProvider(), "gemini" => new GeminiProvider(), _ => new OpenAiProvider() };
        await foreach (var _ in provider.StreamChatAsync(new(config, [new ChatMessage("user", "Describe") { Images = [image] }]))) { }
        switch (protocol)
        {
            case "openai": Assert.Equal(image.DataUrl, captured.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()); break;
            case "responses": Assert.Equal(image.DataUrl, captured.GetProperty("input")[0].GetProperty("content")[1].GetProperty("image_url").GetString()); break;
            case "anthropic": Assert.Equal(Png, captured.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("source").GetProperty("data").GetString()); break;
            case "gemini": Assert.Equal(Png, captured.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData").GetProperty("data").GetString()); break;
        }
    }

    [Fact]
    public async Task ImageReferencesAreBinaryAndPersistAcrossStoreReload()
    {
        using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Root, "pixel image.png");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(Png));
        var prompt = await PromptReferences.ExpandWithImagesAsync("Describe @\"pixel image.png\"", workspace.Root);
        Assert.Equal(Png, Assert.Single(prompt.Images).Base64); Assert.Contains("Attached image", prompt.Text);
        var data = Path.Combine(workspace.Root, "data"); var session = new Conversation();
        using (var store = new DataStore(data)) { store.AddConversation(session); store.AddMessage(session.Id, new Message { Role = "user", ConversationId = session.Id, Content = prompt.Text, Images = prompt.Images.ToList() }); }
        using var reopened = new DataStore(data);
        Assert.Equal(Png, Assert.Single(Assert.Single(reopened.GetMessages(session.Id)).Images).Base64);
        Assert.Throws<ArgumentException>(() => ImageAttachment.FromBytes("fake.png", new byte[20]));
        Assert.Throws<ArgumentException>(() => ImageAttachment.Validate(Enumerable.Repeat(prompt.Images[0], 9).ToArray()));
    }
}
