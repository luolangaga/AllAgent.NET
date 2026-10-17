using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class AgentRoleEditorTests
{
    [Fact]
    public async Task ModelSelectionAndBudgetClearingPersistOnlyOnSave()
    {
        var path = Path.Combine(Path.GetTempPath(), "AllAgent-role-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            Guid configId;
            using (var store = new DataStore(path))
            {
                var config = new ApiConfig { DisplayName = "Local", DefaultModel = "first", Models = ["first", "second"] };
                store.AddApiConfig(config); configId = config.Id;
                var role = new AgentRoleDefinition("custom", "Custom", "Task", Execution: new() { MaxOutputTokens = 100 });
                var script = new Script(["提供商与模型", "Local", "second", "执行预算", "单次输出 Token", "保存并返回"], ["inherit"]);
                var editor = new AgentRoleEditor(() => store.ApiConfigs.ToArray(), script.Choose, script.Text, script.Errors.Add,
                    draft => new AgentRoleCatalog(store).Import(JsonSerializer.Serialize(new[] { draft })));
                Assert.NotNull(await editor.EditAsync(role)); Assert.Empty(script.Errors);
                Assert.Equal(100, role.Execution!.MaxOutputTokens);
            }
            using (var reopened = new DataStore(path))
            {
                var saved = new AgentRoleCatalog(reopened).Get("custom");
                Assert.Equal(configId.ToString(), saved.Provider); Assert.Equal("second", saved.Model);
                Assert.Null(saved.Execution!.MaxOutputTokens);
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task CancelDiscardsAllDraftChanges()
    {
        var script = new Script(["上下文来源", "不包含", "上下文来源", "包含", "取消修改"]);
        var role = new AgentRoleDefinition("general", "General", "Task");
        var saved = false;
        var editor = new AgentRoleEditor(() => [], script.Choose, script.Text, script.Errors.Add, _ => saved = true);
        Assert.Null(await editor.EditAsync(role)); Assert.False(saved); Assert.Null(role.Execution);
    }

    [Fact]
    public async Task InvalidNumericInputKeepsEditorOpenAndPreservesPreviousBudget()
    {
        var script = new Script(["执行预算", "超时秒数", "执行预算", "超时秒数", "保存并返回"], ["oops", "90"]);
        var editor = new AgentRoleEditor(() => [], script.Choose, script.Text, script.Errors.Add);
        var saved = await editor.EditAsync(new("general", "General", "Task", Execution: new() { MaxTotalTokens = 1000 }));
        Assert.Single(script.Errors); Assert.Equal(90, saved!.Execution!.TimeoutSeconds); Assert.Equal(1000, saved.Execution.MaxTotalTokens);
    }

    [Fact]
    public async Task SaveRejectionKeepsDraftAvailableForAnotherAttempt()
    {
        var script = new Script(["角色说明与指令", "保存并返回", "保存并返回"], ["New description", "New instructions"]);
        var attempts = 0;
        var editor = new AgentRoleEditor(() => [], script.Choose, script.Text, script.Errors.Add, _ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("Role is still running");
        });
        var saved = await editor.EditAsync(new("custom", "Original", "Task"));
        Assert.Equal(2, attempts); Assert.Single(script.Errors); Assert.Equal("New instructions", saved!.Instructions);
    }

    [Fact]
    public async Task ReadOnlyBuiltinCannotBeWeakened()
    {
        var script = new Script(["工具与技能权限", "只读模式", "保存并返回"]);
        var editor = new AgentRoleEditor(() => [], script.Choose, script.Text, script.Errors.Add);
        var saved = await editor.EditAsync(new("review", "Review", "Task", ReadOnly: true));
        Assert.True(saved!.ReadOnly); Assert.Single(script.Errors);
    }

    [Fact]
    public async Task FallbackCanBeSelectedFromConfiguredModels()
    {
        var config = new ApiConfig { DisplayName = "Backup", DefaultModel = "backup" };
        var script = new Script(["失败处理", "切换备用模型", "Backup", "backup", "保存并返回"]);
        var editor = new AgentRoleEditor(() => [config], script.Choose, script.Text, script.Errors.Add);
        var saved = await editor.EditAsync(new("custom", "Custom", "Task"));
        Assert.Empty(script.Errors); Assert.Equal("fallback", saved!.Execution!.FailurePolicy);
        Assert.Equal(config.Id.ToString(), saved.Execution.FallbackProvider); Assert.Equal("backup", saved.Execution.FallbackModel);
    }

    private sealed class Script(string[] choices, string[]? text = null)
    {
        private readonly Queue<string> _choices = new(choices);
        private readonly Queue<string> _text = new(text ?? []);
        public List<string> Errors { get; } = [];
        public string Choose(string title, string[] items)
        {
            Assert.NotEmpty(_choices);
            var choice = _choices.Dequeue();
            return Assert.Single(items, item => item.StartsWith(choice, StringComparison.Ordinal));
        }
        public Task<string> Text(string label, string? current) => Task.FromResult(_text.Dequeue());
    }
}
