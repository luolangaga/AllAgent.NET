using System.Net;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.WorkBuddy;
using Xunit;

namespace AllAgent.NET.Tests;

public class WorkBuddyAutomationTests
{
    [Fact]
    public async Task CheckinAndActivityReportWriteHistory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "allagent-workbuddy-auto-" + Guid.NewGuid());
        var token = Token("cn-user");
        var checkins = 0;
        var reports = 0;
        using var account = await CreateAccountAsync(directory, new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/billing/meter/daily-checkin")
            {
                checkins++;
                Assert.Equal("www.codebuddy.cn", request.RequestUri.Host);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                return (HttpStatusCode.OK, """{"code":0,"msg":"ok"}""");
            }
            if (path == "/v2/report")
            {
                reports++;
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("chat_request_send", body);
                Assert.Contains("cn-user", body);
                return (HttpStatusCode.OK, """{"code":0}""");
            }
            if (path == "/activity/growth/streak")
                return (HttpStatusCode.OK, """{"code":0,"data":{"streak":{"days":3}}}""");
            return (HttpStatusCode.OK, """{"code":0}""");
        }), false, token);
        try
        {
            var result = await account.Automation.CheckinAndReportAsync("manual", default);
            Assert.Contains("签到成功", result);
            Assert.Contains("连续打卡 3 天", result);
            Assert.Equal(1, checkins);
            Assert.Equal(1, reports);
            var history = File.ReadAllLines(Path.Combine(directory, "activity_history.jsonl"));
            Assert.Equal(2, history.Length);
            Assert.Contains("\"task\":\"checkin\"", history[0]);
            Assert.Contains("\"task\":\"activity\"", history[1]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GrowthTasksReportAndClaim()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "allagent-workbuddy-auto-" + Guid.NewGuid());
        var token = Token("cn-user");
        var reported = false;
        var reports = 0;
        var claims = 0;
        using var account = await CreateAccountAsync(directory, new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/activity/growth/tasks")
            {
                var status = reported ? "completed" : "accepted";
                var current = reported ? 1 : 0;
                var body = """{"code":0,"data":{"tasks":[{"task_code":"create_canvas","title":"创建设计任务","accept_status":"STATUS","task_type":"single","progress":{"current":CURRENT,"target":1},"reward_credit":300}]}}"""
                    .Replace("STATUS", status).Replace("CURRENT", current.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return (HttpStatusCode.OK, body);
            }
            if (path == "/v2/report")
            {
                reports++;
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("wbx_design_canvas_task_create", body);
                Assert.Contains("cn-user", body);
                reported = true;
                return (HttpStatusCode.OK, """{"code":0}""");
            }
            if (path == "/activity/growth/tasks/create_canvas/claim")
            {
                claims++;
                return (HttpStatusCode.OK, """{"code":0,"data":{"credit":300}}""");
            }
            if (path == "/activity/growth/buddy/travel/status")
                return (HttpStatusCode.OK, """{"code":0,"data":{"state":"idle","daily_limit_reached":true}}""");
            if (path == "/activity/growth/heatmap")
                return (HttpStatusCode.OK, """{"code":0,"data":{"cells":[]}}""");
            if (path == "/activity/growth/streak")
                return (HttpStatusCode.OK, """{"code":0,"data":{"streak":{"days":1},"makeup_cards":{"balance":0},"redemption_status":{"tiers":[]}}}""");
            if (path == "/activity/growth/lottery/summary")
                return (HttpStatusCode.OK, """{"code":0,"data":{"chances":0}}""");
            if (path.StartsWith("/billing/meter/claim-", StringComparison.Ordinal))
                return (HttpStatusCode.OK, """{"code":10000,"msg":"already claimed"}""");
            return (HttpStatusCode.OK, """{"code":0}""");
        }), false, token);
        try
        {
            var result = await account.Automation.RunGrowthAsync("manual", default);
            Assert.Contains("点亮并领奖成功", result);
            Assert.Contains("+300", result);
            Assert.Equal(1, reports);
            Assert.Equal(1, claims);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CatTravelClaimsArrivedReward()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "allagent-workbuddy-auto-" + Guid.NewGuid());
        var token = Token("cn-user");
        using var account = await CreateAccountAsync(directory, new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/activity/growth/buddy/travel/status")
                return Task.FromResult((HttpStatusCode.OK, """{"code":0,"data":{"state":"arrived"}}"""));
            if (path == "/activity/growth/buddy/travel/claim")
                return Task.FromResult((HttpStatusCode.OK, """{"code":0,"data":{"reward_credit":50}}"""));
            return Task.FromResult((HttpStatusCode.OK, """{"code":0}"""));
        }), false, token);
        try
        {
            var result = await account.Automation.CatTravelAsync("manual", default);
            Assert.Contains("领奖成功", result);
            Assert.Contains("50", result);
            Assert.Contains("cat_travel", File.ReadAllText(Path.Combine(directory, "activity_history.jsonl")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InternationalCycleRunsDailyChatOncePerDay()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "allagent-workbuddy-auto-" + Guid.NewGuid());
        var token = Token("intl-user");
        var chats = 0;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "automation-settings.json"), """{"useWebDailyChat":false,"runOnStartup":false}""");
        using var account = await CreateAccountAsync(directory, new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v2/chat/completions")
            {
                chats++;
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("deepseek-v4.1-flash", body);
                return (HttpStatusCode.OK, "data: {\"choices\":[]}\n\n");
            }
            return (HttpStatusCode.OK, """{"code":0}""");
        }), true, token);
        try
        {
            var first = await account.Automation.RunCycleAsync("manual", default);
            Assert.Contains("每日活跃对话成功完成", first);
            var second = await account.Automation.RunCycleAsync("manual", default);
            Assert.Contains("今日已完成", second);
            Assert.Equal(1, chats);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task<WorkBuddyAccount> CreateAccountAsync(string directory, HttpMessageHandler handler, bool international, string token)
    {
        var vault = new TokenVault(Path.Combine(directory, "credentials"));
        await vault.SaveAsync(international ? "workbuddy-intl" : "workbuddy", new OAuthTokens(token, "refresh", DateTimeOffset.UtcNow.AddHours(6)), CancellationToken.None);
        var account = new WorkBuddyAccount(directory, international, new HttpClient(handler));
        account.Automation.ThrottleDelay = TimeSpan.Zero;
        account.Automation.SettleDelay = TimeSpan.Zero;
        return account;
    }

    private static string Token(string sub)
    {
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { sub, exp = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "header." + payload + ".signature";
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<(HttpStatusCode Status, string Body)>> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = await reply(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
