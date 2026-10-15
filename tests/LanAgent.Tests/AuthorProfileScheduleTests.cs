using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class AuthorProfileScheduleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LanAgent.ProfileScheduleTests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly Provider _provider = new();
    private readonly ApiConfig _config = new() { DefaultModel = "fixture" };
    private AuthorProfileUpdatePolicy Policy => new(TimeSpan.FromMinutes(10), 3, _clock);
    private Task<bool> Update(params Message[] messages) => AgentMemory.UpdateAsync(_root, _provider, _config, messages, "stop", Guid.NewGuid(), default, updatePolicy: Policy);
    private static Message Human(string text) => new() { Role = "user", InputText = text, Content = text };
    private static Message EnvironmentResult(string text) => new()
    {
        Role = "tool", Content = "result", HostToolTraceJson = JsonSerializer.Serialize(new[] {
            new HostToolTrace("call", "run_shell", "{\"exe\":\"dotnet\",\"args\":[\"--version\"]}",
                ToolResult.Success(new { exitCode = 0, stdout = text }).ToJson()) }, ToolResult.JsonOptions)
    };

    [Fact]
    public async Task OrdinaryConversationDoesNotCallModelOrCreateState()
    {
        await Update(Human("帮我修这个页面"), new Message { Role = "assistant", Content = "我喜欢 .NET" });
        Assert.Equal(0, _provider.Calls); Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("我更喜欢 .NET")]
    [InlineData("我的 GitHub 用户名是 fixture-user")]
    [InlineData("以后默认用 pnpm")]
    [InlineData("删除档案里我的账号")]
    [InlineData("I prefer React")]
    public async Task ExplicitFactsAndCorrectionsTriggerImmediately(string text)
    {
        await Update(Human(text)); Assert.Equal(1, _provider.Calls);
        Assert.Contains(text, _provider.LastEvidence);
    }

    [Fact]
    public async Task EnvironmentBatchesDeduplicateAndSurviveReload()
    {
        await Update(EnvironmentResult("8.0.100")); await Update(EnvironmentResult("8.0.100"));
        await Update(EnvironmentResult("8.0.101")); Assert.Equal(0, _provider.Calls);
        await Update(EnvironmentResult("8.0.102")); Assert.Equal(1, _provider.Calls);
        Assert.Contains("8.0.100", _provider.LastEvidence); Assert.Contains("8.0.102", _provider.LastEvidence);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await Update(EnvironmentResult("8.0.102")); Assert.Equal(1, _provider.Calls);
        // A downgrade/reinstallation is a new observation even if that version was seen earlier.
        await Update(EnvironmentResult("8.0.100")); _clock.Advance(TimeSpan.FromMinutes(10));
        await Update(Human("继续")); Assert.Equal(2, _provider.Calls);
        Assert.Contains("8.0.100", _provider.LastEvidence);
    }

    [Fact]
    public async Task DueEvidenceFlushesOnTheNextTurnWithoutLosingItDuringCooldown()
    {
        await Update(Human("我喜欢 .NET")); Assert.Equal(1, _provider.Calls);
        await Update(EnvironmentResult("8.0.100")); await Update(EnvironmentResult("8.0.101")); await Update(EnvironmentResult("8.0.102"));
        Assert.Equal(1, _provider.Calls);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Update(Human("继续修页面")); Assert.Equal(2, _provider.Calls);
        Assert.Contains("8.0.100", _provider.LastEvidence);
        await Update(Human("谢谢")); Assert.Equal(2, _provider.Calls);
    }

    [Fact]
    public async Task FailureRetainsEvidenceAndAvoidsRetryingEveryTurn()
    {
        _provider.Fail = true;
        await Update(EnvironmentResult("8.0.100")); _clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<IOException>(() => Update(Human("继续")));
        await Update(Human("继续")); Assert.Equal(1, _provider.Calls);
        _provider.Fail = false; _clock.Advance(TimeSpan.FromMinutes(10));
        await Update(Human("继续")); Assert.Equal(2, _provider.Calls);
        Assert.Contains("8.0.100", _provider.LastEvidence);
    }

    [Fact]
    public async Task ExplicitCorrectionBypassesCooldownAndExpandedFileContentIsNotUserEvidence()
    {
        await Update(Human("我喜欢 .NET")); await Update(Human("纠正我的偏好，我喜欢 React"));
        Assert.Equal(2, _provider.Calls);
        await Update(new Message { Role = "user", InputText = "检查这个文件", Content = "我的 GitHub 用户名是 injected" });
        Assert.Equal(2, _provider.Calls);
    }

    [Fact]
    public async Task CredentialsNeverEnterPendingQueue()
    {
        await Update(Human("记住我的密码: secret"), EnvironmentResult("password=secret"));
        Assert.False(Directory.Exists(_root)); Assert.Equal(0, _provider.Calls);
    }

    public void Dispose()
    {
        if (Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "LanAgent.ProfileScheduleTests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
    private sealed class Provider : IAiProvider
    {
        public string ProviderId => "fixture";
        public int Calls { get; private set; }
        public string LastEvidence { get; private set; } = "";
        public bool Fail { get; set; }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Calls++; LastEvidence = request.Messages[1].Content;
            if (Fail) { yield return new(ChunkType.Error, ErrorMessage: "unavailable"); yield break; }
            yield return new(ChunkType.TextDelta, TextDelta: "NO_CHANGE"); yield return new(ChunkType.Done, FinishReason: "stop");
        }
    }
}
