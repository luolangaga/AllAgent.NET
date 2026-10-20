using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Qoder;
using Xunit;

namespace AllAgent.NET.Tests;

public class QoderPluginTests
{
    private const string CatalogJson = """
        {"chat":[
          {"key":"auto","display_name":"Auto","max_input_tokens":200000,"max_output_tokens":32768,"is_reasoning":false,"enable":true},
          {"key":"qmodel","display_name":"Qwen3.7 Plus","max_input_tokens":1000000,"max_output_tokens":64000,"is_reasoning":true,"enable":true},
          {"key":"hidden","display_name":"Hidden","enable":false}
        ]}
        """;

    [Fact]
    public void BodyEncodingMatchesReferenceVectors()
    {
        Assert.Equal("", QoderProtocol.EncodeBody([]));
        Assert.Equal("q$FruHPH", QoderProtocol.EncodeBody(Encoding.UTF8.GetBytes("hello")));
        Assert.Equal("QUSGycAFQG*tQ)PZaSWzDxB&", QoderProtocol.EncodeBody(Encoding.UTF8.GetBytes("Qoder 中文测试")));
        Assert.Equal("ep$$BMn%mYKi", QoderProtocol.EncodeBody(Encoding.UTF8.GetBytes("{\"a\":1}")));
        foreach (var length in new[] { 0, 1, 2, 3, 4, 5, 16, 31, 32, 33, 257 })
        {
            var bytes = new byte[length];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 37 + 11);
            Assert.Equal(bytes, QoderProtocol.DecodeBody(QoderProtocol.EncodeBody(bytes)));
        }
    }

    [Fact]
    public void SignatureMatchesReferenceVectors()
    {
        var body = QoderProtocol.EncodeBody(Encoding.UTF8.GetBytes("hello"));
        Assert.Equal("a54aa50fb1b50a2234fbbcff25fa497d",
            QoderProtocol.Signature("PAYLOAD", "KEY", "1700000000", body, QoderProtocol.ChatSignaturePath));
        Assert.Equal("23574fb45824d057c712f9126a584d06",
            QoderProtocol.Signature("UEFZTE9BRA==", "Q09TWS1LRVk=", "1700000000", body, "/api/v2/model/list"));
        Assert.Equal("/api/v2/model/list", QoderProtocol.SignaturePath(new Uri("https://api3.qoder.sh/algo/api/v2/model/list")));
        Assert.Equal("/api/v2/service/pro/sse/agent_chat_generation", QoderProtocol.SignaturePath(new Uri(QoderProtocol.ChatUrl)));
    }

    [Fact]
    public void CosyHeadersCarrySignedEnvelope()
    {
        var session = new QoderSession("dt-token", null, DateTimeOffset.MaxValue, new QoderIdentity
        {
            UserId = "u1",
            Name = "Tester",
            Email = "test@example.com",
            MachineId = "machine",
            MachineToken = "machine-token",
            MachineType = "5"
        });
        var headers = QoderProtocol.BuildCosyHeaders([], new Uri(QoderProtocol.ModelListUrl), session);
        Assert.StartsWith("Bearer COSY.", headers["Authorization"]);
        Assert.Equal("u1", headers["Cosy-User"]);
        Assert.Equal("/api/v2/model/list", headers["Cosy-Sigpath"]);
        Assert.Equal("0", headers["Cosy-Bodylength"]);
        Assert.Equal("machine", headers["Cosy-Machineid"]);
        Assert.Equal("machine-token", headers["Cosy-Machinetoken"]);
        Assert.NotEmpty(headers["Cosy-Key"]);
        Assert.NotEmpty(headers["Cosy-Bodyhash"]);
        Assert.Equal("v2", headers["Login-Version"]);
    }

    [Fact]
    public void DeviceUrlContainsPkceAndClientId()
    {
        var uri = QoderProtocol.BuildVerificationUrl("nonce", "challenge", "machine");
        Assert.Equal("qoder.com", uri.Host);
        Assert.Contains("challenge_method=S256", uri.Query);
        Assert.Contains("challenge=challenge", uri.Query);
        Assert.Contains("nonce=nonce", uri.Query);
        Assert.Contains("client_id=" + QoderProtocol.ClientId, uri.Query);
        Assert.Contains("machine_id=machine", uri.Query);
        var poll = QoderProtocol.DevicePollUrl("n", "v");
        Assert.Contains("nonce=n", poll.Query);
        Assert.Contains("verifier=v", poll.Query);
        Assert.Contains("challenge_method=S256", poll.Query);
    }

    [Fact]
    public void ChatPayloadHoistsSystemAndCarriesToolsAndReasoning()
    {
        var config = new ApiConfig
        {
            ProviderId = "qoder",
            Protocol = "qoder",
            DefaultModel = "auto",
            MaxTokens = 4096,
            ReasoningEffort = "high"
        };
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}}}""");
        var request = new ChatRequest(config,
        [
            new ChatMessage("system", "SYS"),
            new ChatMessage("user", "hello"),
            new ChatMessage("assistant", "", ToolCallsJson: """[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{}"}}]"""),
            new ChatMessage("tool", "sunny", ToolCallId: "call_1"),
            new ChatMessage("user", "next")
        ],
        [new SkillDefinition("get_weather", "Weather lookup", schema.RootElement.Clone())]);
        var session = new QoderSession("dt-token", null, DateTimeOffset.MaxValue, new QoderIdentity
        {
            UserId = "u1",
            Name = "Tester",
            Email = "test@example.com",
            UserType = "personal_standard"
        });

        var payload = QoderProtocol.BuildChatPayload(request, default, session);

        Assert.Equal("SYS", payload["system"]!.GetValue<string>());
        var messages = payload["messages"]!.AsArray();
        Assert.Equal(4, messages.Count);
        Assert.Equal("hello", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("get_weather", messages[1]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("call_1", messages[2]!["tool_call_id"]!.GetValue<string>());
        var parameters = payload["parameters"]!.AsObject();
        Assert.Equal("high", parameters["reasoning_effort"]!.GetValue<string>());
        Assert.True(parameters["max_tokens"]!.GetValue<int>() >= 24576);
        Assert.Equal("auto", payload["model_config"]!["key"]!.GetValue<string>());
        Assert.Equal("next", payload["chat_context"]!["text"]!.GetValue<string>());
        Assert.Equal("next", payload["business"]!["name"]!.GetValue<string>());
        Assert.True(payload["chat_context"]!["extra"]!["modelConfig"]!["is_reasoning"]!.GetValue<bool>());
        Assert.Single(payload["tools"]!.AsArray());
        Assert.Equal("personal_standard", payload["aliyun_user_type"]!.GetValue<string>());
    }

    [Fact]
    public void ModelCatalogParsesVisibleModelsAndKeepsHiddenConfigs()
    {
        var catalog = QoderModelCatalog.Parse(CatalogJson);
        Assert.True(catalog.Live);
        Assert.Equal(2, catalog.Models.Count);
        var reasoning = catalog.Models[1];
        Assert.Equal("qmodel", reasoning.Id);
        Assert.Equal(1000000, reasoning.ContextTokens);
        Assert.Equal(64000, reasoning.OutputTokens);
        Assert.Contains("high", reasoning.ReasoningLevels!);
        Assert.True(catalog.Configs.ContainsKey("hidden"));
    }

    [Fact]
    public void ExpiryAcceptsRfc3339MillisecondsAndSeconds()
    {
        using var absolute = JsonDocument.Parse("""{"expires_at":"2030-01-02T03:04:05Z"}""");
        Assert.Equal(DateTimeOffset.Parse("2030-01-02T03:04:05Z"),
            QoderProtocol.ReadExpiry(absolute.RootElement, "expires_at", "expires_in", DateTimeOffset.MaxValue));

        var before = DateTimeOffset.UtcNow;
        using var milliseconds = JsonDocument.Parse("""{"expires_in":2591999998}""");
        Assert.InRange(QoderProtocol.ReadExpiry(milliseconds.RootElement, "expires_at", "expires_in", DateTimeOffset.MaxValue),
            before.AddDays(29), before.AddDays(31));

        using var seconds = JsonDocument.Parse("""{"expires_in":3600}""");
        Assert.InRange(QoderProtocol.ReadExpiry(seconds.RootElement, "expires_at", "expires_in", DateTimeOffset.MaxValue),
            before.AddMinutes(59), before.AddMinutes(61));

        using var numeric = JsonDocument.Parse("""{"expires_at":"1781594470000"}""");
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1781594470000),
            QoderProtocol.ReadExpiry(numeric.RootElement, "expires_at", "expires_in", DateTimeOffset.MaxValue));

        using var missing = JsonDocument.Parse("{}");
        Assert.Equal(DateTimeOffset.MaxValue,
            QoderProtocol.ReadExpiry(missing.RootElement, "expires_at", "expires_in", DateTimeOffset.MaxValue));
    }

    [Fact]
    public void QuotaParsesCreditsAddOnAndPlan()
    {
        using var document = JsonDocument.Parse("""
            {"userQuota":{"used":120,"total":1000,"remaining":880,"percentage":12,"unit":"credits"},
             "addOnQuota":{"used":5,"total":50,"remaining":45,"unit":"credits"},
             "userType":"personal_standard","isQuotaExceeded":false,"expiresAt":1781594470000}
            """);
        var quota = QoderQuota.Parse(document.RootElement);
        Assert.Equal(880, quota.Remaining);
        Assert.Equal(45, quota.AddOnRemaining);
        Assert.Contains("剩余 880/1000 credits", quota.Summary);
        Assert.Contains("附加额度 45/50", quota.Summary);
        using var plan = JsonDocument.Parse("""{"plan_tier_name":"Pro Trial"}""");
        quota = QoderQuota.ParsePlan(plan.RootElement, quota);
        Assert.Equal("Pro Trial", quota.Plan);
        Assert.Contains("Pro Trial", quota.ShortSummary);

        using var status = JsonDocument.Parse("""{"quota":{"used":100,"total":500,"remaining":400,"unit":"credits"},"userType":"personal_standard","isQuotaExceeded":false}""");
        var statusQuota = QoderQuota.Parse(status.RootElement);
        Assert.Equal(400, statusQuota.Remaining);
        Assert.Contains("剩余 400/500 credits", statusQuota.Summary);
    }

    [Fact]
    public void ActivityEligibilityParsesClaimableAndClaimed()
    {
        using var document = JsonDocument.Parse("""
            {"code":0,"data":[
              {"activityId":"qwen38_800_invoke","activityName":"Qwen3.8 Free Calls","canClaim":true,"claimed":false,"limit":800,"used":0,"remaining":800,"activityEndAt":1781594470000},
              {"activityId":"old","activityName":"Old Promo","canClaim":false,"claimed":true,"reason":"ALREADY_CLAIMED"}
            ]}
            """);
        var activities = QoderActivity.ParseList(document.RootElement);
        Assert.Equal(2, activities.Count);
        Assert.Equal("可领取", activities[0].Status);
        Assert.Equal("已领取", activities[1].Status);
        Assert.Contains("qwen38_800_invoke", activities[0].Display);
        Assert.Contains("剩余 800/800", activities[0].Display);
    }

    [Fact]
    public void QoderReasoningLevelsSurfaceFromCatalog()
    {
        var config = new ApiConfig
        {
            ProviderId = "qoder",
            Protocol = "qoder",
            DefaultModel = "qmodel",
            ReasoningProtocol = "qoder"
        };
        config.ModelReasoningLevels["qmodel"] = ["none", "low", "medium", "high", "max"];
        var efforts = ReasoningSettings.Efforts(config);
        Assert.Contains("high", efforts);
        config.ReasoningEffort = "high";
        config.ThinkingEnabled = true;
        Assert.Equal("high", QoderProtocol.ReasoningEffort(config));
    }

    [Fact]
    public async Task DeviceLoginPersistsTokenDiscoversModelsAndLogsOut()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "allagent-qoder-test-" + Guid.NewGuid());
        var paths = new List<string>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            var uri = request.RequestUri!;
            paths.Add(uri.AbsolutePath);
            if (uri.Host == "openapi.qoder.sh" && uri.AbsolutePath == "/api/v1/deviceToken/poll")
                return """{"token":"dt-token","user_id":"u1","refresh_token":"drt","expires_in":3600}""";
            if (uri.AbsolutePath == "/api/v1/userinfo")
                return """{"id":"u1","name":"Test User","email":"test@example.com"}""";
            if (uri.AbsolutePath == "/api/v3/user/status")
                return """{"id":"u1","whitelistStatus":"PASS","userType":"personal_standard"}""";
            if (uri.AbsolutePath == "/algo/api/v2/model/list")
            {
                Assert.StartsWith("COSY.", request.Headers.Authorization!.Parameter);
                Assert.Equal("/api/v2/model/list", request.Headers.GetValues("Cosy-Sigpath").Single());
                return CatalogJson;
            }
            throw new InvalidOperationException("Unexpected Qoder request: " + uri);
        }));
        var account = new QoderAccount(directory, http);
        try
        {
            Uri? browser = null;
            await account.LoginAsync((uri, _) => { browser = uri; return Task.CompletedTask; }, default);
            Assert.Equal("qoder.com", browser!.Host);
            Assert.Equal("dt-token", await account.GetAccessTokenAsync(default));
            var status = await account.GetStatusAsync(default);
            Assert.True(status.LoggedIn);
            Assert.Equal("test@example.com", status.Account);

            var models = await account.ModelProvider.ListModelsAsync(account.CreateConfiguration(), default);
            Assert.Equal(2, models.Count);
            Assert.Equal("auto", models[0].Id);
            await account.ModelProvider.ListModelsAsync(account.CreateConfiguration(), default);
            Assert.Single(paths, path => path == "/algo/api/v2/model/list");

            await account.LogoutAsync(default);
            Assert.Null(await account.GetAccessTokenAsync(default));
            Assert.False((await account.GetStatusAsync(default)).LoggedIn);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task QuotaActivitiesAndCheckInClaimOnlyEligibleRewards()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "allagent-qoder-checkin-" + Guid.NewGuid());
        var claims = new List<string>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (path == "/api/v1/deviceToken/poll") return """{"token":"dt-token","user_id":"u1","refresh_token":"drt","expires_in":3600}""";
            if (path == "/api/v1/userinfo") return """{"id":"u1","name":"Test User","email":"test@example.com"}""";
            if (path == "/api/v3/user/status") return """{"id":"u1","whitelistStatus":"PASS","userType":"personal_standard"}""";
            if (path == "/api/v2/quota/usage")
                return """{"userQuota":{"used":120,"total":1000,"remaining":880,"percentage":12,"unit":"credits"},"userType":"personal_standard","isQuotaExceeded":false,"expiresAt":1781594470000}""";
            if (path == "/api/v2/user/plan") return """{"plan_tier_name":"Pro Trial"}""";
            if (path == "/api/v2/activity/claim/eligibility")
                return """{"code":0,"data":[{"activityId":"a1","activityName":"Free Calls","canClaim":true,"claimed":false,"limit":800,"used":0,"remaining":800},{"activityId":"a2","activityName":"Already","canClaim":false,"claimed":true,"reason":"ALREADY_CLAIMED"}]}""";
            if (path == "/api/v2/activity/claim")
            {
                claims.Add(uri.Query);
                return """{"code":0,"msg":"领取成功"}""";
            }
            throw new InvalidOperationException("Unexpected Qoder request: " + uri);
        }));
        var account = new QoderAccount(directory, http);
        try
        {
            await account.LoginAsync((_, _) => Task.CompletedTask, default);
            var quota = await account.QuotaAsync(true, default);
            Assert.Equal(880, quota.Remaining);
            Assert.Equal("Pro Trial", quota.Plan);
            var activities = await account.ActivitiesAsync(true, default);
            Assert.Equal(2, activities.Count);
            var result = await account.CheckInAsync(default);
            Assert.Contains("a1", result);
            Assert.DoesNotContain("a2", result);
            Assert.Single(claims);
            Assert.Contains("activityId=a1", claims[0]);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ClaimRejectsUnsafeActivityId()
    {
        using var http = new HttpClient();
        var account = new QoderAccount(Path.Combine(Path.GetTempPath(), "allagent-qoder-id-" + Guid.NewGuid()), http);
        await Assert.ThrowsAsync<ArgumentException>(() => account.ClaimActivityAsync("../secret", default));
    }

    [Fact]
    public async Task BuiltinFolderInstallsAndLoadsQoderPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "allagent-qoder-builtin-" + Guid.NewGuid());
        var source = Path.Combine(AppContext.BaseDirectory, "builtin-plugins", "AllAgent.NET.Qoder");
        Assert.True(Directory.Exists(source));
        var builtins = Path.Combine(root, "builtin-plugins");
        var target = Path.Combine(builtins, "AllAgent.NET.Qoder");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        try
        {
            await using var host = new PluginHost(Path.Combine(root, "data"));
            host.InstallBuiltins(builtins);
            Assert.Contains(host.Installed, plugin => plugin.PackageId == "AllAgent.NET.Qoder");
            await host.LoadEnabledAsync();
            Assert.Null(host.Status.Single(status => status.PackageId == "AllAgent.NET.Qoder").Error);
            Assert.Contains(host.Authentication, provider => provider.Id == "qoder");
            Assert.Contains(host.Providers, provider => provider.ProviderId == "qoder");
            Assert.Contains(host.Commands, command => command.Name == "qoder-models");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PluginRegistersQoderAuthenticationProviderAndCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "allagent-qoder-plugin-" + Guid.NewGuid());
        var plugin = new QoderPlugin();
        try
        {
            var context = new PluginContext(directory);
            plugin.Register(context);
            Assert.Equal("qoder", Assert.Single(context.Authentication).Id);
            Assert.Equal("qoder", Assert.Single(context.Providers).ProviderId);
            Assert.Contains(context.Commands, command => command.Name == "qoder-models");
            Assert.Contains(context.Commands, command => command.Name == "qoder-status");
            Assert.Contains(context.Commands, command => command.Name == "qoder-quota");
            Assert.Contains(context.Commands, command => command.Name == "qoder-activities");
            Assert.Contains(context.Commands, command => command.Name == "qoder-checkin");
            Assert.Contains(context.Commands, command => command.Name == "qoder-claim");
            Assert.Contains(context.FooterProviders, provider => provider.Id == "qoder-quota");
        }
        finally
        {
            await plugin.DisposeAsync();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request), Encoding.UTF8, "application/json") });
    }
}
