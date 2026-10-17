using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ContextSummarizerTests
{
    [Fact]
    public async Task AllImageBatchesReceiveEvidenceAndPreviousConclusions()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");
        var images = Enumerable.Range(0, 9).Select(i => ImageAttachment.FromBytes($"evidence-{i}.png", png.Concat(new[] { (byte)i }).ToArray())).ToArray();
        var provider = new Provider();
        var result = await ContextSummarizer.SummarizeAsync(provider, new ApiConfig(), "Summarize", "Historical objective", images, 8000, Guid.NewGuid(), default);
        Assert.Equal("summary-2", result); Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(8, provider.Requests[0].Messages[1].Images.Count); Assert.Single(provider.Requests[1].Messages[1].Images);
        Assert.Contains("summary-1", provider.Requests[1].Messages[1].Content);
        Assert.Contains("evidence-8.png", provider.Requests[1].Messages[1].Content);
        Assert.Equal(images.Select(i => i.Base64), provider.Requests.SelectMany(r => r.Messages[1].Images).Select(i => i.Base64));
        Assert.All(provider.Requests, request => Assert.Empty(request.Tools!));
    }
    [Fact]
    public async Task InterruptedSummaryCannotBeAccepted()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ContextSummarizer.SummarizeAsync(new Provider(incomplete: true), new ApiConfig(), "Summarize", "History", [], 8000, Guid.NewGuid(), default));
    }
    [Fact]
    public async Task UnexpectedToolCallCannotBeAccepted()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ContextSummarizer.SummarizeAsync(new Provider(tool: true), new ApiConfig(), "Summarize", "History", [], 8000, Guid.NewGuid(), default));
    }
    private sealed class Provider(bool incomplete = false, bool tool = false) : IAiProvider
    {
        public string ProviderId => "fixture";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            yield return new(ChunkType.TextDelta, TextDelta: "summary-" + Requests.Count);
            if (tool) yield return new(ChunkType.ToolCallDelta, ToolCall: new("bad", "execute_code", "{}"));
            if (!incomplete) yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
