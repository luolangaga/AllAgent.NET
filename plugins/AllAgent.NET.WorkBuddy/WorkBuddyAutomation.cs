using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AllAgent.NET.WorkBuddy;

public sealed record WorkBuddyAutomationResult(bool Ok, string Message, double EarnedCredit = 0);

/// <summary>Daily automation for one WorkBuddy realm: scheduler, mainland task suite and activity history.</summary>
public sealed class WorkBuddyAutomation : IDisposable
{
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";
    private const string DailyChatModel = "deepseek-v4.1-flash";
    private readonly WorkBuddyAccount account;
    private readonly string directory;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private static readonly object SharedStateGate = new();
    private static readonly object SharedHistoryGate = new();
    private readonly object logGate = new();
    private readonly List<string> logs = [];
    private CancellationTokenSource? schedulerCancellation;
    private Task? schedulerLoop;
    private DateTimeOffset? lastRun;
    public TimeSpan ThrottleDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(1.5);
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public WorkBuddyAutomation(WorkBuddyAccount account, string directory)
    {
        this.account = account;
        this.directory = directory;
    }

    private string HistoryPath => Path.Combine(directory, "activity_history.jsonl");
    private string StatePath => Path.Combine(directory, "automation-state.json");
    private string SettingsPath => Path.Combine(directory, "automation-settings.json");

    public void Start()
    {
        lock (logGate)
        {
            if (schedulerLoop != null) return;
            schedulerCancellation = new CancellationTokenSource();
            schedulerLoop = Task.Run(() => SchedulerLoopAsync(schedulerCancellation.Token));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        Task? loop;
        lock (logGate) { cancellation = schedulerCancellation; loop = schedulerLoop; schedulerCancellation = null; schedulerLoop = null; }
        cancellation?.Cancel();
        try { loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        cancellation?.Dispose();
        cycleGate.Dispose();
    }

    private async Task SchedulerLoopAsync(CancellationToken ct)
    {
        try
        {
            var settings = LoadSettings();
            if (settings.Enabled && settings.RunOnStartup)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                await RunScheduledCycleAsync("启动初次巡检", ct);
            }
            while (!ct.IsCancellationRequested)
            {
                settings = LoadSettings();
                var now = Clock();
                if (settings.Enabled && now.Minute == 0 && settings.Hours.Contains(now.Hour))
                {
                    await RunScheduledCycleAsync("整点排程 " + now.ToString("HH") + ":00", ct);
                    await Task.Delay(TimeSpan.FromSeconds(65), ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("调度器异常：" + SafeMessage(ex.Message)); }
    }

    private async Task RunScheduledCycleAsync(string trigger, CancellationToken ct)
    {
        if (!await cycleGate.WaitAsync(0, ct))
        {
            AddLog("跳过 " + trigger + "：上一轮仍在执行");
            return;
        }
        try { await CycleCoreAsync(trigger, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("巡检失败：" + SafeMessage(ex.Message)); }
        finally { cycleGate.Release(); }
    }

    public Task<string> RunCycleAsync(string trigger, CancellationToken ct) => WithGateAsync(() => CycleCoreAsync(trigger, ct), ct);

    public Task<string> CheckinAndReportAsync(string trigger, CancellationToken ct) => WithGateAsync(async () =>
    {
        var token = await account.GetAccessTokenAsync(ct);
        if (token == null) return LoginRequired();
        var lines = new List<string> { account.Name + " 每日签到与活跃上报（" + trigger + "）" };
        var checkin = await CheckinCoreAsync(token, trigger, ct);
        lines.Add("签到：" + checkin.Message);
        if (!account.International && !WorkBuddyAccount.IsEnterprise(token))
        {
            var report = await ReportActivityCoreAsync(token, trigger, ct);
            lines.Add("活跃：" + report.Message);
        }
        return string.Join(Environment.NewLine, lines);
    }, ct);

    public Task<string> RunGrowthAsync(string trigger, CancellationToken ct) => WithGateAsync(() => GrowthCoreAsync(trigger, ct), ct);

    public Task<string> CatTravelAsync(string trigger, CancellationToken ct) => WithGateAsync(async () =>
    {
        var token = await account.GetAccessTokenAsync(ct);
        if (token == null) return LoginRequired();
        return (await CatTravelCoreAsync(token, trigger, ct)).Message;
    }, ct);

    public Task<string> StreakBonusAsync(string trigger, CancellationToken ct) => WithGateAsync(async () =>
    {
        var token = await account.GetAccessTokenAsync(ct);
        if (token == null) return LoginRequired();
        var (lines, _) = await StreakCoreAsync(token, trigger, ct);
        return string.Join(Environment.NewLine, lines);
    }, ct);

    public Task<string> HistoryAsync(int limit, CancellationToken ct) => Task.Run(() =>
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(HistoryPath)) return "暂无 WorkBuddy 自动化历史。";
            var all = File.ReadAllLines(HistoryPath);
            var realm = account.International ? "intl" : "cn";
            for (var i = all.Length - 1; i >= 0 && lines.Count < limit; i--)
            {
                ct.ThrowIfCancellationRequested();
                var text = all[i].Trim();
                if (text.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var row = document.RootElement;
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    if (StringProperty(row, "realm") != realm) continue;
                    var task = StringProperty(row, "task");
                    var trigger = StringProperty(row, "trigger");
                    var ok = row.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                    var message = SafeMessage(StringProperty(row, "message"));
                    lines.Add(StringProperty(row, "ts") + " · " + realm + " · " + task + " · " + trigger + " · " + (ok ? "✓" : "✗") + " · " + message);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) { return "读取自动化历史失败：" + SafeMessage(ex.Message); }
        return lines.Count == 0 ? "暂无 WorkBuddy 自动化历史。" : string.Join(Environment.NewLine, lines);
    }, ct);

    public string Status()
    {
        var settings = LoadSettings();
        var state = LoadState();
        var realmState = account.International ? state.Intl : state.Cn;
        var next = ComputeNext(settings, Clock());
        var lines = new List<string>
        {
            account.Name + " 自动化：" + (settings.Enabled ? "已启用" : "已停用"),
            "排程小时：" + string.Join(", ", settings.Hours.Select(hour => hour.ToString("00") + ":00")),
            "上次巡检：" + (lastRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未运行"),
            "下次巡检：" + (next?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未排程"),
            account.International
                ? "每日活跃：" + (DoneToday(realmState.LastDailyChat) ? "今日已完成" : "今日待执行")
                : "每日签到：" + (DoneToday(realmState.LastCheckin) ? "今日已完成" : "今日待执行") +
                  " · 活跃上报：" + (DoneToday(realmState.LastActivityReport) ? "今日已完成" : "今日待执行"),
            "网页打卡通道：" + (settings.UseWebDailyChat ? "启用" : "关闭") + " · 启动巡检：" + (settings.RunOnStartup ? "启用" : "关闭")
        };
        string[] recent;
        lock (logGate) recent = logs.ToArray();
        if (recent.Length == 0) lines.Add("最近日志：无");
        else
        {
            lines.Add("最近日志：");
            lines.AddRange(recent.Reverse().Take(10).Select(line => "  " + line));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> WithGateAsync(Func<Task<string>> action, CancellationToken ct)
    {
        try
        {
            if (!await cycleGate.WaitAsync(0, ct)) return account.Name + " 自动化正在执行，请稍后再试。";
        }
        catch (OperationCanceledException) { return "操作已取消。"; }
        try { return await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var message = "自动化失败：" + SafeMessage(ex.Message);
            AddLog(message);
            Record("automation", "manual", false, message, null);
            return message;
        }
        finally { cycleGate.Release(); }
    }

    private async Task<string> CycleCoreAsync(string trigger, CancellationToken ct)
    {
        var lines = new List<string> { account.Name + " 自动巡检（" + trigger + "）" };
        string? token;
        try { token = await account.GetAccessTokenAsync(ct); }
        catch (Exception ex)
        {
            var error = "读取凭据失败：" + SafeMessage(ex.Message);
            lines.Add(error);
            AddLog(error);
            return string.Join(Environment.NewLine, lines);
        }
        if (token == null)
        {
            var missing = "未登录：请先 /login " + account.Id;
            lines.Add(missing);
            AddLog(missing);
            return string.Join(Environment.NewLine, lines);
        }
        try
        {
            var refreshed = await account.KeepAliveAsync(TimeSpan.FromHours(2), ct);
            lines.Add(refreshed ? "Token 保活：已刷新" : "Token 保活：有效期充足");
        }
        catch (Exception ex) { lines.Add("Token 保活失败：" + SafeMessage(ex.Message)); }

        if (account.International)
        {
            var state = LoadState();
            if (DoneToday(state.Intl.LastDailyChat)) lines.Add("每日活跃：今日已完成");
            else
            {
                var daily = await DailyChatCoreAsync(token, trigger, ct);
                lines.Add("每日活跃：" + daily.Message);
            }
        }
        else if (WorkBuddyAccount.IsEnterprise(token))
        {
            lines.Add("企业版账号无个人成长体系，跳过签到与任务。");
        }
        else
        {
            var state = LoadState();
            if (DoneToday(state.Cn.LastCheckin)) lines.Add("每日签到：今日已完成");
            else lines.Add("每日签到：" + (await CheckinCoreAsync(token, trigger, ct)).Message);
            state = LoadState();
            if (DoneToday(state.Cn.LastActivityReport)) lines.Add("活跃上报：今日已完成");
            else lines.Add("活跃上报：" + (await ReportActivityCoreAsync(token, trigger, ct)).Message);
            var travel = await CatTravelCoreAsync(token, trigger, ct);
            lines.Add("猫猫旅行：" + travel.Message);
            var streak = await StreakCoreAsync(token, trigger, ct);
            lines.AddRange(streak.Lines.Select(line => "连登管家：" + line));
            var settings = LoadSettings();
            if (settings.Hours.Contains(Clock().Hour) && (Clock().Hour >= 23 || Clock().Hour < 8))
                lines.Add("夜猫子：" + await NightGrowthCoreAsync(token, trigger, ct));
        }
        lastRun = Clock();
        var summary = string.Join("；", lines.Skip(1));
        AddLog(lines[0] + " — " + summary);
        return string.Join(Environment.NewLine, lines);
    }

    private string LoginRequired() => "请先 /login " + account.Id;

    private async Task<WorkBuddyAutomationResult> CheckinCoreAsync(string token, string trigger, CancellationToken ct)
    {
        if (account.International) return new(false, "国际版不适用每日签到。");
        if (WorkBuddyAccount.IsEnterprise(token)) return new(true, "企业版账号无个人成长体系，跳过签到。");
        var result = await SendAsync(JsonRequest(new Uri(account.BillingBase + "/v2/billing/meter/daily-checkin"), HttpMethod.Post, token, new { }, true), ct);
        var ok = result.Ok && result.Code is 0 or 10001;
        var message = result.Message;
        if (!ok && result.Status == 400 && (result.Code == 10001 || MessageContains(message, "已签到"))) ok = true;
        var text = ok
            ? (string.IsNullOrWhiteSpace(message) || message == "ok" || message.StartsWith('{') ? "签到成功" : message)
            : "签到失败：" + (string.IsNullOrWhiteSpace(message) ? "HTTP " + result.Status : message);
        Record("checkin", trigger, ok, text, token);
        if (ok) Mark(state => state.Cn.LastCheckin = LocalStamp());
        return new(ok, text);
    }

    private async Task<WorkBuddyAutomationResult> ReportActivityCoreAsync(string token, string trigger, CancellationToken ct)
    {
        if (account.International) return new(false, "国际版不适用对话活跃上报。");
        if (WorkBuddyAccount.IsEnterprise(token)) return new(true, "企业版账号无个人成长体系，跳过活跃上报。");
        var uid = WorkBuddyAccount.Uid(token);
        var result = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/v2/report"), HttpMethod.Post, token, new[] { BuildEvent("chat", uid, 0, null) }), ct);
        var ok = result.Ok && result.Code == 0;
        string text;
        if (!ok) text = "活跃上报失败：" + Describe(result);
        else
        {
            var days = await TryGetStreakDaysAsync(token, ct);
            text = days == null ? "对话活跃上报成功（连登天数未知）"
                : days <= 0 ? "对话活跃上报成功，但连登仍是 0 天（可能被上游静默丢弃）"
                : "对话活跃上报成功（连续打卡 " + days + " 天）";
        }
        Record("activity", trigger, ok, text, token);
        if (ok) Mark(state => state.Cn.LastActivityReport = LocalStamp());
        return new(ok, text);
    }

    private async Task<int?> TryGetStreakDaysAsync(string token, CancellationToken ct)
    {
        try
        {
            var result = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/streak"), HttpMethod.Get, token, null), ct);
            if (!result.Ok || result.Body.ValueKind != JsonValueKind.Object) return null;
            var data = DataElement(result.Body);
            if (data.ValueKind != JsonValueKind.Object) return null;
            var streak = ObjectElement(data, "streak");
            return streak.ValueKind == JsonValueKind.Object ? (int)Number(streak, "days") : null;
        }
        catch { return null; }
    }

    private async Task<string> GrowthCoreAsync(string trigger, CancellationToken ct)
    {
        if (account.International) return "国际版不适用国内成长任务中心。";
        var token = await account.GetAccessTokenAsync(ct);
        if (token == null) return LoginRequired();
        if (WorkBuddyAccount.IsEnterprise(token)) return "企业版账号无个人成长体系，跳过成长任务。";
        var lines = new List<string> { account.Name + " 成长任务（" + trigger + "）" };
        var (tasks, error) = await FetchGrowthTasksAsync(token, ct);
        if (tasks.Count == 0)
        {
            var message = string.IsNullOrEmpty(error) ? "未能获取到任务清单，请检查网络或账号状态。" : "获取任务清单失败：" + error;
            AddLog(message);
            Record("growth", trigger, false, message, token);
            return message;
        }

        double earned = 0;
        foreach (var task in tasks.Where(IsAutoTask))
            earned += await ProgressOneAsync(token, task, lines, ct);

        var unaccepted = tasks.Where(task => task.Status == "not_accepted" && !task.Unforgeable && !IsAutoTask(task)).Select(task => task.Code).ToList();
        if (unaccepted.Count > 0)
        {
            lines.Add("发现 " + unaccepted.Count + " 个待接取任务，正在批量接取...");
            var accepted = await AcceptTasksAsync(token, unaccepted, ct);
            if (accepted.Accepted.Count > 0) lines.Add("✓ 已接取 " + accepted.Accepted.Count + " 个任务");
            if (accepted.Failed.Count > 0) lines.Add("! 接取未成功 " + accepted.Failed.Count + " 个：" + string.Join(", ", accepted.Failed.Take(5)) + (string.IsNullOrEmpty(accepted.Message) ? "" : "（" + accepted.Message + "）"));
            await DelayAsync(ThrottleDelay, ct);
            var refreshed = await FetchGrowthTasksAsync(token, ct);
            if (refreshed.Tasks.Count > 0) tasks = refreshed.Tasks;
            var pending = tasks.Count(task => task.Status == "not_accepted" && !task.Unforgeable && !IsAutoTask(task));
            if (pending > 0) lines.Add("! 仍有 " + pending + " 个任务未接取，本轮跳过这些任务");
        }

        foreach (var task in tasks.Where(task => !IsAutoTask(task)))
            earned += await ProgressOneAsync(token, task, lines, ct);

        var travel = await CatTravelCoreAsync(token, trigger, ct);
        lines.Add("猫猫旅行：" + travel.Message);
        if (travel.EarnedCredit > 0) earned += travel.EarnedCredit;
        var streak = await StreakCoreAsync(token, trigger, ct);
        lines.AddRange(streak.Lines.Select(line => "连登管家：" + line));
        lines.Add("本次新增到账：+" + earned.ToString("0.##") + " 积分");
        Record("growth", trigger, true, string.Join("；", lines.Skip(1)), token);
        AddLog(lines[0] + " — 新增 " + earned.ToString("0.##") + " 积分");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<(List<GrowthTask> Tasks, string Error)> FetchGrowthTasksAsync(string token, CancellationToken ct)
    {
        var result = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/v2/activity/growth/tasks"), HttpMethod.Get, token, null), ct);
        if (!result.Ok || result.Code is { } code && code != 0) return ([], Describe(result));
        var data = DataElement(result.Body);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("tasks", out var items) || items.ValueKind != JsonValueKind.Array) return ([], "");
        var tasks = new List<GrowthTask>();
        foreach (var item in items.EnumerateArray())
        {
            var taskCode = StringProperty(item, "task_code");
            if (string.IsNullOrWhiteSpace(taskCode)) continue;
            var spec = SpecFor(taskCode);
            var progress = ObjectElement(item, "progress");
            var current = Number(progress, "current");
            var target = Number(progress, "target");
            var title = StringProperty(item, "title");
            tasks.Add(new GrowthTask(
                taskCode,
                string.IsNullOrWhiteSpace(title) ? spec?.Name ?? taskCode : title,
                StringProperty(item, "jump_url"),
                StringProperty(item, "accept_status") is { Length: > 0 } status ? status : "not_accepted",
                StringProperty(item, "task_type"),
                current,
                target > 0 ? target : spec?.Target ?? 1,
                spec?.Reason,
                Number(item, "reward_credit"),
                Number(item, "reward_energy")));
        }
        return (tasks, "");
    }

    private async Task<(List<string> Accepted, List<string> Failed, string Message)> AcceptTasksAsync(string token, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var accepted = new List<string>();
        var failed = new List<string>();
        var message = "";
        for (var offset = 0; offset < codes.Count; offset += 20)
        {
            var part = codes.Skip(offset).Take(20).ToArray();
            var result = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/v2/activity/growth/tasks/accept"), HttpMethod.Post, token, new Dictionary<string, object?> { ["task_codes"] = part }), ct);
            if (!result.Ok || result.Code is { } code && code != 0)
            {
                failed.AddRange(part);
                message = Describe(result);
                continue;
            }
            var data = DataElement(result.Body);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var taskCode = StringProperty(item, "task_code");
                    seen.Add(taskCode);
                    var status = StringProperty(item, "status");
                    if (status is "accepted" or "already_accepted") accepted.Add(taskCode);
                    else
                    {
                        failed.Add(taskCode);
                        var reason = StringProperty(item, "message");
                        if (!string.IsNullOrEmpty(reason)) message = taskCode + ": " + reason;
                    }
                }
            }
            failed.AddRange(part.Where(partCode => !seen.Contains(partCode)));
        }
        return (accepted, failed, SafeMessage(message));
    }

    private async Task<double> ProgressOneAsync(string token, GrowthTask task, List<string> logs, CancellationToken ct)
    {
        var spec = SpecFor(task.Code);
        if (task.Status == "claimed") return 0;
        var target = task.Target > 0 ? task.Target : spec?.Target ?? 1;
        var name = string.IsNullOrWhiteSpace(task.Name) ? task.Code : task.Name;
        if (task.Status == "completed" || task.Current >= target)
        {
            var claim = await ClaimTaskAsync(token, task.Code, ct);
            if (claim.Ok)
            {
                logs.Add("✓ 任务 [" + name + "] 领奖成功: +" + claim.Credit.ToString("0.##") + " 积分");
                await DelayAsync(ThrottleDelay, ct);
                return claim.Credit;
            }
            logs.Add("! 任务 [" + name + "] 领奖失败: " + claim.Message);
            await DelayAsync(ThrottleDelay, ct);
            return 0;
        }
        if (task.Unforgeable)
        {
            logs.Add("⏭ 任务 [" + name + "] 需真实操作完成: " + task.Reason + ", 跳过");
            return 0;
        }
        if (DesktopOnlyTasks.TryGetValue(task.Code, out var desktopOnly))
        {
            logs.Add("⏭ 任务 [" + name + "] 需真实操作完成: " + desktopOnly + (string.IsNullOrEmpty(task.JumpUrl) ? "" : " (深链 " + task.JumpUrl + ")") + ", 跳过事件伪造");
            return 0;
        }
        if (task.Code == "black_cat" && !InNightWindow())
        {
            logs.Add("🌙 任务 [" + name + "] 仅 23:00-08:00 上报计数, 当前不在窗口, 跳过");
            return 0;
        }
        if (task.Status == "not_accepted" && !IsAutoTask(task))
        {
            logs.Add("⏭ 任务 [" + name + "] 仍未接取, 跳过");
            return 0;
        }
        if (spec == null) return 0;

        var uid = WorkBuddyAccount.Uid(token);
        var need = Math.Max(1, (int)Math.Ceiling(target - task.Current));
        logs.Add("正在点亮任务 [" + name + "] (需上报 " + need + " 次)...");
        var reportOk = true;
        for (var i = 0; i < need; i++)
        {
            (string Id, string Name)? expert = null;
            if (spec.Kind is "expert" or "team")
            {
                var pool = spec.Kind == "team" ? TeamPool : ExpertPool;
                expert = pool[((int)task.Current + i) % pool.Length];
            }
            if (!await ReportEventsAsync(token, new[] { BuildEvent(spec.Kind, uid, i, expert) }, ct)) reportOk = false;
            if (i < need - 1) await DelayAsync(ThrottleDelay, ct);
        }
        if (!reportOk) logs.Add("! 任务 [" + name + "] 部分事件上报失败");
        await DelayAsync(SettleDelay, ct);

        var progress = task.Current;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (freshTasks, _) = await FetchGrowthTasksAsync(token, ct);
            var fresh = freshTasks.FirstOrDefault(item => item.Code == task.Code);
            if (fresh != null)
            {
                progress = fresh.Current;
                if (progress >= target || fresh.Status is "completed" or "claimed") break;
                if (progress > task.Current) { await DelayAsync(SettleDelay, ct); continue; }
            }
            if (attempt < 2) await DelayAsync(SettleDelay, ct);
            else break;
        }
        if (progress < target)
        {
            logs.Add("? 任务 [" + name + "] 已上报但进度 " + progress.ToString("0.##") + "/" + target.ToString("0.##") + " 未达成, 领奖顺延到下次运行");
            await DelayAsync(ThrottleDelay, ct);
            return 0;
        }
        var claimed = await ClaimTaskAsync(token, task.Code, ct);
        if (claimed.Ok)
        {
            logs.Add("✓ 任务 [" + name + "] 点亮并领奖成功: +" + claimed.Credit.ToString("0.##") + " 积分");
            await DelayAsync(ThrottleDelay, ct);
            return claimed.Credit;
        }
        logs.Add("! 任务 [" + name + "] 进度已达但领奖失败: " + claimed.Message);
        await DelayAsync(ThrottleDelay, ct);
        return 0;
    }

    private async Task<bool> ReportEventsAsync(string token, IEnumerable<Dictionary<string, object?>> events, CancellationToken ct)
    {
        var result = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/v2/report"), HttpMethod.Post, token, events.ToArray()), ct);
        return result.Ok && result.Code == 0;
    }

    private async Task<(bool Ok, double Credit, string Message)> ClaimTaskAsync(string token, string code, CancellationToken ct)
    {
        var path = "/activity/growth/tasks/" + Uri.EscapeDataString(code) + "/claim";
        var result = await SendAsync(JsonRequest(new Uri(account.ChatBase + path), HttpMethod.Post, token, new { }), ct);
        if (result.Ok && result.Code == 0) return (true, Number(DataElement(result.Body), "credit"), "ok");
        if (result.Status == 400)
        {
            var web = await ClaimViaWebAsync(token, path, ct);
            if (web.Ok) return web;
        }
        return (false, 0, Describe(result));
    }

    private async Task<(bool Ok, double Credit, string Message)> ClaimViaWebAsync(string token, string path, CancellationToken ct)
    {
        var uid = WorkBuddyAccount.Uid(token);
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token,
            ["Accept"] = "application/json, text/plain, */*",
            ["x-client-platform"] = "web",
            ["Origin"] = account.WebBase,
            ["Referer"] = account.WebBase + "/profile/growth-center",
            ["User-Agent"] = WebUserAgent,
            ["X-User-Id"] = uid,
            ["X-Domain"] = account.WebBase
        };
        var result = await SendAsync(WebRequest(new Uri(account.WebBase + path), HttpMethod.Post, new { }, headers), ct);
        if (result.Ok && result.Code == 0) return (true, Number(DataElement(result.Body), "credit"), "ok");
        return (false, 0, Describe(result));
    }

    private async Task<WorkBuddyAutomationResult> CatTravelCoreAsync(string token, string trigger, CancellationToken ct)
    {
        if (account.International) return new(false, "国际版不适用猫猫旅行。");
        if (WorkBuddyAccount.IsEnterprise(token)) return new(false, "企业版账号无个人成长体系，跳过猫猫旅行。");
        var status = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/buddy/travel/status"), HttpMethod.Get, token, null), ct);
        if (!status.Ok || status.Code is { } statusCode && statusCode != 0)
        {
            var failure = "查询旅行状态失败：" + Describe(status);
            Record("cat_travel", trigger, false, failure, token);
            return new(false, failure);
        }
        var data = DataElement(status.Body);
        var state = StringProperty(data, "state");
        WorkBuddyAutomationResult result;
        if (state == "arrived")
        {
            var claim = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/buddy/travel/claim"), HttpMethod.Post, token, new { }), ct);
            var credit = Number(DataElement(claim.Body), "reward_credit");
            result = claim.Ok && claim.Code == 0
                ? new(true, "旅行归来领奖成功！获得 " + credit.ToString("0.##") + " 积分", credit)
                : new(false, "旅行领奖失败：" + Describe(claim));
        }
        else if (state == "idle")
        {
            if (data.TryGetProperty("daily_limit_reached", out var limited) && limited.ValueKind == JsonValueKind.True)
                result = new(true, "猫猫今日已完成旅行，明日 00:00 刷新。");
            else
            {
                var locationId = await FirstTravelLocationAsync(token, ct);
                var depart = await DepartTravelAsync(token, locationId, ct);
                if (!depart.Ok && MessageContains(depart.Message, "no active buddy"))
                {
                    if (await EnsureBuddyAsync(token, ct)) depart = await DepartTravelAsync(token, locationId, ct);
                    else
                    {
                        Record("cat_travel", trigger, false, "还没有 Buddy", token);
                        return new(false, "还没有 Buddy：需先完成并领取「领取一只 Buddy」任务。");
                    }
                }
                result = depart;
            }
        }
        else if (state == "traveling") result = new(true, "猫猫正在旅行途中，请稍后再来查看！");
        else result = new(true, "当前状态: " + (string.IsNullOrEmpty(state) ? "unknown" : state));
        Record("cat_travel", trigger, result.Ok, result.Message, token);
        return result;
    }

    private async Task<object> FirstTravelLocationAsync(string token, CancellationToken ct)
    {
        var config = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/buddy/travel/config"), HttpMethod.Get, token, null), ct);
        var data = DataElement(config.Body);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
            foreach (var location in locations.EnumerateArray())
            {
                if (location.TryGetProperty("id", out var id)) return id.ValueKind == JsonValueKind.Number ? id.GetInt64() : id.ToString();
            }
        return 1;
    }

    private async Task<WorkBuddyAutomationResult> DepartTravelAsync(string token, object locationId, CancellationToken ct)
    {
        var result = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/buddy/travel/depart"), HttpMethod.Post, token, new Dictionary<string, object?> { ["location_id"] = locationId }), ct);
        if (result.Ok && result.Code == 0)
        {
            var location = ObjectElement(DataElement(result.Body), "location");
            var name = StringProperty(location, "name");
            return new(true, "猫猫已出发前往「" + (string.IsNullOrEmpty(name) ? "目的地" : name) + "」，预计数小时后归来！");
        }
        return new(false, "派出旅行失败：" + Describe(result));
    }

    private async Task<bool> EnsureBuddyAsync(string token, CancellationToken ct)
    {
        var (tasks, _) = await FetchGrowthTasksAsync(token, ct);
        var buddy = tasks.FirstOrDefault(task => task.Code == "first_buddy");
        if (buddy == null) return false;
        if (buddy.Status == "claimed") return true;
        var logs = new List<string>();
        await ProgressOneAsync(token, buddy, logs, ct);
        var (refreshed, _) = await FetchGrowthTasksAsync(token, ct);
        return refreshed.Any(task => task.Code == "first_buddy" && task.Status == "claimed");
    }

    private async Task<(List<string> Lines, bool Ok)> StreakCoreAsync(string token, string trigger, CancellationToken ct)
    {
        var lines = new List<string>();
        if (account.International) return (new List<string> { "国际版不适用连登管家。" }, false);
        if (WorkBuddyAccount.IsEnterprise(token)) return (new List<string> { "企业版账号无个人成长体系，跳过连登管家。" }, false);

        var heatmap = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/heatmap"), HttpMethod.Get, token, null), ct);
        if (heatmap.Ok && heatmap.Code == 0)
        {
            var cells = DataElement(heatmap.Body);
            if (cells.ValueKind == JsonValueKind.Object && cells.TryGetProperty("cells", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var yesterday = Clock().AddDays(-1).ToString("yyyy-MM-dd");
                foreach (var cell in list.EnumerateArray())
                {
                    if (StringProperty(cell, "date") != yesterday || Number(cell, "score") != 0) continue;
                    var streak = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/streak"), HttpMethod.Get, token, null), ct);
                    var cards = Number(ObjectElement(DataElement(streak.Body), "makeup_cards"), "balance");
                    if (cards > 0)
                    {
                        var used = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/makeup-cards/use"), HttpMethod.Post, token, new Dictionary<string, object?> { ["target_date"] = yesterday }), ct);
                        if (used.Ok && used.Code == 0) lines.Add("已消耗 1 张补签卡补签昨日 (" + yesterday + ")");
                    }
                    break;
                }
            }
        }

        foreach (var (path, name) in new[] { ("/billing/meter/claim-gift", "新手礼包"), ("/billing/meter/claim-compensation", "活动补偿") })
        {
            var gift = await SendAsync(JsonRequest(new Uri(account.BillingBase + path), HttpMethod.Post, token, new { }, true), ct);
            if (gift.Ok && gift.Code == 0)
            {
                var credit = Number(DataElement(gift.Body), "credit");
                lines.Add("领取" + name + " +" + credit.ToString("0.##") + " 积分");
            }
        }

        var streakStatus = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/streak"), HttpMethod.Get, token, null), ct);
        if (streakStatus.Ok && streakStatus.Code == 0)
        {
            var redemption = ObjectElement(DataElement(streakStatus.Body), "redemption_status");
            if (redemption.ValueKind == JsonValueKind.Object && redemption.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
                foreach (var tierItem in tiers.EnumerateArray())
                {
                    var tier = tierItem.ValueKind == JsonValueKind.String ? tierItem.GetString() : StringProperty(tierItem, "tier");
                    if (string.IsNullOrWhiteSpace(tier)) continue;
                    var status = StringProperty(redemption, "tier_" + tier + "_status");
                    if (status is "locked" or "claimed") continue;
                    var redeem = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/redeem"), HttpMethod.Post, token, new Dictionary<string, object?>
                    {
                        ["tier"] = tier,
                        ["client_token"] = Guid.NewGuid().ToString()
                    }), ct);
                    if (redeem.Ok && redeem.Code == 0) lines.Add("成功兑换连登 " + tier + " 档位");
                }
        }

        var lottery = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/lottery/summary"), HttpMethod.Get, token, null), ct);
        if (lottery.Ok && lottery.Code == 0)
        {
            var chances = (int)Number(DataElement(lottery.Body), "chances");
            var drawn = 0;
            for (var i = 0; i < chances; i++)
            {
                var draw = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/activity/growth/lottery/draw"), HttpMethod.Post, token, new Dictionary<string, object?> { ["client_token"] = Guid.NewGuid().ToString() }), ct);
                if (!draw.Ok || draw.Code != 0) break;
                drawn++;
                await DelayAsync(ThrottleDelay, ct);
            }
            if (drawn > 0) lines.Add("完成成长抽奖 " + drawn + " 次");
        }

        var ok = true;
        if (lines.Count > 0) Record("streak", trigger, ok, string.Join("；", lines), token);
        return (lines.Count == 0 ? new List<string> { "连登状态正常（无需兑换/无抽奖次数）。" } : lines, ok);
    }

    private async Task<string> NightGrowthCoreAsync(string token, string trigger, CancellationToken ct)
    {
        if (account.International) return "国际版不适用。";
        if (WorkBuddyAccount.IsEnterprise(token)) return "企业版账号无个人成长体系。";
        if (!InNightWindow()) return "当前不在 23:00-08:00 夜间窗口，跳过。";
        var (tasks, _) = await FetchGrowthTasksAsync(token, ct);
        var task = tasks.FirstOrDefault(item => item.Code == "black_cat");
        if (task == null) return "未获取到夜猫子任务清单。";
        if (task.Status == "claimed") return "夜猫子任务已领奖。";
        if (task.Status == "not_accepted")
        {
            var accepted = await AcceptTasksAsync(token, ["black_cat"], ct);
            if (!accepted.Accepted.Contains("black_cat")) return "夜猫子任务未接取，今晚跳过。";
            (tasks, _) = await FetchGrowthTasksAsync(token, ct);
            task = tasks.FirstOrDefault(item => item.Code == "black_cat") ?? task;
        }
        if (task.Current >= task.Target)
        {
            var claim = await ClaimTaskAsync(token, "black_cat", ct);
            var message = claim.Ok ? "夜猫子任务达标，领奖 +" + claim.Credit.ToString("0.##") + " 积分。" : "夜猫子任务领奖失败：" + claim.Message;
            Record("night_growth", trigger, claim.Ok, message, token);
            return message;
        }
        var reported = await ReportEventsAsync(token, new[] { BuildEvent("cat", WorkBuddyAccount.Uid(token), 0, null) }, ct);
        if (!reported) return "夜猫子事件上报失败。";
        var progress = task.Current;
        for (var i = 0; i < 4; i++)
        {
            await DelayAsync(TimeSpan.FromSeconds(4), ct);
            (tasks, _) = await FetchGrowthTasksAsync(token, ct);
            var fresh = tasks.FirstOrDefault(item => item.Code == "black_cat");
            if (fresh == null) break;
            progress = fresh.Current;
            if (progress > task.Current) break;
        }
        if (progress >= task.Target)
        {
            var claim = await ClaimTaskAsync(token, "black_cat", ct);
            var message = claim.Ok ? "夜猫子任务完成，领奖 +" + claim.Credit.ToString("0.##") + " 积分。" : "夜猫子任务领奖失败：" + claim.Message;
            Record("night_growth", trigger, claim.Ok, message, token);
            return message;
        }
        var result = progress > task.Current
            ? "今晚 +1 (" + progress.ToString("0.##") + "/" + task.Target.ToString("0.##") + ")，明晚继续。"
            : "已上报但进度暂未变化 (" + progress.ToString("0.##") + "/" + task.Target.ToString("0.##") + ")。";
        Record("night_growth", trigger, true, result, token);
        return result;
    }

    private async Task<WorkBuddyAutomationResult> DailyChatCoreAsync(string token, string trigger, CancellationToken ct)
    {
        if (!account.International) return new(false, "每日活跃对话仅适用于国际版。");
        var desktop = await SendAsync(JsonRequest(new Uri(account.ChatBase + "/v2/chat/completions"), HttpMethod.Post, token, new Dictionary<string, object?>
        {
            ["model"] = DailyChatModel,
            ["messages"] = new object[] { new Dictionary<string, object?> { ["role"] = "user", ["content"] = "Hi" } },
            ["stream"] = true,
            ["max_tokens"] = 10,
            ["reasoning_effort"] = "none"
        }), ct);
        var desktopOk = desktop.Ok;
        var settings = LoadSettings();
        var webOk = true;
        var webMessage = "";
        if (settings.UseWebDailyChat)
        {
            var web = await WebDailyChatAsync(token, ct);
            webOk = web.Ok;
            webMessage = web.Message;
        }
        var ok = settings.UseWebDailyChat ? webOk : desktopOk;
        var text = ok
            ? webOk && settings.UseWebDailyChat ? "每日活跃对话成功完成（" + webMessage + "）" : "每日活跃对话成功完成。"
            : "每日活跃对话未完成：" + (settings.UseWebDailyChat ? "网页通道失败：" + webMessage : "桌面通道失败：" + Describe(desktop));
        Record("daily_chat", trigger, ok, text, token);
        if (ok) Mark(state => state.Intl.LastDailyChat = LocalStamp());
        return new(ok, text);
    }

    private async Task<(bool Ok, string Message)> WebDailyChatAsync(string token, CancellationToken ct)
    {
        var uid = WorkBuddyAccount.Uid(token);
        var origin = account.WebBase;
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token,
            ["X-User-Id"] = uid,
            ["Accept"] = "application/json, text/plain, */*",
            ["Origin"] = origin,
            ["Referer"] = origin + "/app",
            ["User-Agent"] = WebUserAgent
        };
        var created = await SendAsync(WebRequest(new Uri(origin + "/console/as/conversations/"), HttpMethod.Post, new Dictionary<string, object?>
        {
            ["prompt"] = "Hi",
            ["model"] = DailyChatModel,
            ["conversationOrigin"] = "workbuddy-app",
            ["plugins"] = new object[] { new Dictionary<string, object?> { ["name"] = "weixinpay", ["marketplace"] = "codebuddy-builtin" } }
        }, headers), ct);
        if (!created.Ok) return (false, "创建会话失败：HTTP " + created.Status + " " + created.Message);
        if (created.Code is { } code && code != 0) return (false, "创建会话失败：" + Describe(created));
        var conversation = StringProperty(DataElement(created.Body), "id");
        if (string.IsNullOrEmpty(conversation)) return (false, "创建会话失败：响应缺少会话 id");

        var sessionResult = await SendAsync(WebRequest(new Uri(origin + "/console/as/conversations/" + Uri.EscapeDataString(conversation) + "/session"), HttpMethod.Get, null, headers), ct);
        if (!sessionResult.Ok) return (false, "查询沙箱失败：HTTP " + sessionResult.Status);
        var session = DataElement(sessionResult.Body);
        var link = StringProperty(session, "link");
        if (string.IsNullOrEmpty(link)) link = StringProperty(session, "endpoint");
        var sandboxToken = StringProperty(session, "token");
        var sessionId = StringProperty(session, "sessionId");
        if (string.IsNullOrEmpty(sessionId)) sessionId = StringProperty(session, "session_id");
        if (string.IsNullOrEmpty(sessionId)) sessionId = conversation;
        var cwd = StringProperty(session, "cwd");
        if (string.IsNullOrEmpty(cwd)) cwd = "/workspace";
        if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(sandboxToken)) return (false, "沙箱未就绪（没有 link/token）");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri) || linkUri.Scheme is not ("http" or "https")) return (false, "沙箱地址不可用");

        using var sseClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var connection = await AcpConnection.OpenAsync(sseClient, linkUri, sandboxToken, WebUserAgent, deadline.Token);
        try
        {
            await connection.RequestAsync("initialize", new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new Dictionary<string, object?>
                {
                    ["fs"] = new Dictionary<string, object?> { ["readTextFile"] = false, ["writeTextFile"] = false },
                    ["terminal"] = false
                }
            }, 1, deadline.Token);
            await connection.RequestAsync("session/load", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["cwd"] = cwd,
                ["mcpServers"] = Array.Empty<object>()
            }, 2, deadline.Token);
            try
            {
                await connection.RequestAsync("session/set_model", new Dictionary<string, object?> { ["sessionId"] = sessionId, ["modelId"] = DailyChatModel }, 3, deadline.Token);
            }
            catch { }
            await connection.RequestAsync("session/prompt", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = "Hi" } }
            }, 4, deadline.Token);

            var status = "";
            var started = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(150))
            {
                await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token);
                var poll = await SendAsync(WebRequest(new Uri(origin + "/console/as/conversations/" + Uri.EscapeDataString(conversation)), HttpMethod.Get, null, headers), deadline.Token);
                if (!poll.Ok) continue;
                status = StringProperty(DataElement(poll.Body), "status");
                if (status == "completed") return (true, "网页通道会话跑完：" + connection.Chunks + " 段输出");
                if (status is "failed" or "error") break;
            }
            return (false, "会话在 150s 内没有跑完（状态=" + (string.IsNullOrEmpty(status) ? "未知" : status) + "）");
        }
        catch (OperationCanceledException) { return (false, "网页通道超时"); }
        catch (Exception ex) { return (false, SafeMessage(ex.Message)); }
        finally { await connection.DisposeAsync(); }
    }

    private HttpRequestMessage JsonRequest(Uri uri, HttpMethod method, string? token, object? body, bool billing = false) => account.Request(uri, method, token, body, billing);

    private static HttpRequestMessage WebRequest(Uri uri, HttpMethod method, object? body, IReadOnlyDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    private async Task<HttpResult> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await account.Http.SendAsync(request, ct))
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonElement body = default;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { body = JsonSerializer.Deserialize<JsonElement>(text); } catch (JsonException) { }
            }
            return new((int)response.StatusCode, body, text);
        }
    }

    private sealed record HttpResult(int Status, JsonElement Body, string Text)
    {
        public bool Ok => Status is >= 200 and < 300;
        public int? Code
        {
            get
            {
                if (Body.ValueKind != JsonValueKind.Object || !Body.TryGetProperty("code", out var code)) return null;
                if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)) return number;
                return int.TryParse(code.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            }
        }
        public string Message
        {
            get
            {
                if (Body.ValueKind == JsonValueKind.Object)
                {
                    if (Body.TryGetProperty("msg", out var msg) && !string.IsNullOrWhiteSpace(msg.ToString())) return SafeMessage(msg.ToString());
                    if (Body.TryGetProperty("message", out var message) && !string.IsNullOrWhiteSpace(message.ToString())) return SafeMessage(message.ToString());
                }
                return SafeMessage(Text);
            }
        }
    }

    private static string Describe(HttpResult result)
    {
        var message = result.Message;
        return string.IsNullOrWhiteSpace(message) ? "HTTP " + result.Status : message + " (HTTP " + result.Status + ")";
    }

    private sealed record GrowthTask(string Code, string Name, string JumpUrl, string Status, string TaskType, double Current, double Target, string? Reason, double RewardCredit, double RewardEnergy)
    {
        public bool Unforgeable => !string.IsNullOrEmpty(Reason);
    }

    private sealed record TaskSpec(string Kind, int Target, string Name, string? Reason = null);

    private static readonly Dictionary<string, TaskSpec> TaskSpecs = new(StringComparer.Ordinal)
    {
        ["create_canvas"] = new("canvas", 1, "创建设计任务"),
        ["template_5"] = new("template", 5, "模板创建任务"),
        ["expert_5"] = new("expert", 5, "使用专家助手"),
        ["Expert_team_use_3"] = new("team", 3, "使用专家团队"),
        ["skill_1"] = new("skill", 1, "体验技能"),
        ["automation_1"] = new("automation", 1, "创建自动化任务"),
        ["playbook_prompt"] = new("playbook", 1, "灵感案例使用"),
        ["Expert_lighthouse"] = new("lighthouse", 1, "轻量云专家使用"),
        ["Buddy_App"] = new("buddy5", 1, "进入 Buddy 应用"),
        ["Buddy_App_QQ"] = new("buddy5", 1, "企鹅教师助手"),
        ["Hp_Appearance"] = new("skin", 1, "应用主题外观"),
        ["chat_5"] = new("chat", 5, "发起 5 次对话"),
        ["Model_chat_GLM5.2"] = new("glmchat", 1, "体验 GLM-5.2"),
        ["black_cat"] = new("cat", 3, "夜猫子任务 (23:00-08:00)"),
        ["RichMeow_Chat"] = new("richmeow", 1, "桌面对话事件链"),
        ["Library_read"] = new("library", 1, "浏览资料库"),
        ["first_buddy"] = new("chat", 1, "领取一只 Buddy"),
        ["wb_wechat_oa_subscribe_task"] = new("", 1, "关注官方公众号", "需在微信关注「腾讯 WorkBuddy」公众号满 24 小时"),
        ["Expert_Philanthropy"] = new("", 1, "公益爱心捐赠", "真实捐款动作")
    };

    private static readonly Dictionary<string, string> DesktopOnlyTasks = new(StringComparer.Ordinal)
    {
        ["RichMeow_Chat"] = "在桌面端发起 1 次对话",
        ["Library_read"] = "在桌面端打开「资料库」并读完介绍文档",
        ["Buddy_App"] = "在桌面端左上角「发现应用」进入任意一个 Buddy 应用",
        ["Buddy_App_QQ"] = "在桌面端「发现应用」进入「企鹅教师助手」"
    };

    private static readonly (string Id, string Name)[] ExpertPool =
    [
        ("ex_PZw8Gu81HfN4", "运维工程师"), ("ex_ROsDtJbzADFV", "产品经理"),
        ("ex_SMUnl0nJbPix", "UI设计师"), ("ex_ZTR062oVBOCW", "数据分析师"),
        ("ex_a3sSSFBy8qaC", "后端架构师"), ("ex_aG1kvKbq8lPx", "文案策划"),
        ("ex_al1vxtUOYQ10", "测试专家"), ("ex_cZfiyuET9UQP", "安全顾问"),
        ("ex_eggOvQuVP0hq", "算法工程师"), ("ex_hSwsQjkSKnkX", "前端工程师"),
        ("ex_mMbwwmFA9n9P", "项目管理专家"), ("ex_uAQE5POfk7Zh", "增长运营专家"),
        ("ex_uZzSAScSy7FZ", "行业研究员"), ("ex_LHywGrZOtG7G", "数据分析师"),
        ("ex_NX5C8GBciVed", "测试架构师"), ("ex_DdCsaoq4AtcO", "云端运维专家"),
        ("ex_KzqKQguubrNQ", "内容创作专家"), ("ex_2cvvUZQhDyeJ", "腾讯轻量云专家")
    ];

    private static readonly (string Id, string Name)[] TeamPool =
    [
        ("CloudOpsTeam", "运维专家团队"), ("CloudContentTeam", "内容专家团队"),
        ("CloudDevTeam", "研发专家团队"), ("ProductStrategyTeam", "产品战略团队"),
        ("MarketingCampaignTeam", "营销活动团队"), ("SalesBattleTeam", "销售作战团队"),
        ("DesignEngineTeam", "设计引擎团队"), ("HrOperationsTeam", "人力运营团队")
    ];

    private static TaskSpec? SpecFor(string code) => TaskSpecs.GetValueOrDefault(code);
    private static bool IsAutoTask(GrowthTask task) => task.TaskType == "auto" || task.Code == "first_buddy";

    private static Dictionary<string, object?> BuildEvent(string kind, string uid, int index, (string Id, string Name)? expert)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var conversationId = "wb-task-" + now + "-" + index + "-" + Guid.NewGuid().ToString("N");
        var requestId = conversationId + "-req";
        var eventCode = kind switch
        {
            "canvas" => "wbx_design_canvas_task_create",
            "template" => "agent_task_created_with_template",
            "expert" or "team" or "lighthouse" => "expert_actual_use",
            "skill" => "skill_info",
            "automation" => "automated_task_create_suc",
            "playbook" => "playbook_prompt_send",
            "skin" => "appearance_skin_apply",
            "chat" or "glmchat" or "cat" => "chat_request_send",
            _ => "heartbeat"
        };
        var payload = new Dictionary<string, object?>
        {
            ["eventCode"] = eventCode,
            ["timestamp"] = now,
            ["reportDelay"] = 0,
            ["userId"] = uid
        };
        switch (kind)
        {
            case "canvas":
                payload["conversationId"] = conversationId;
                payload["requestId"] = requestId;
                payload["source"] = "summon_keyword";
                payload["isCustomModel"] = false;
                payload["name"] = "";
                payload["inputLength"] = 12;
                payload["id"] = "wbx-canvas-" + now + "-" + index;
                payload["cost"] = 0;
                payload["isSuccessful"] = true;
                break;
            case "template":
                payload["isCustomModel"] = true;
                payload["id"] = index.ToString(CultureInfo.InvariantCulture);
                payload["name"] = "幻灯片";
                payload["requestId"] = requestId;
                payload["conversationId"] = conversationId;
                break;
            case "expert":
            case "team":
            case "lighthouse":
                var isTeam = kind == "team";
                var expertId = expert?.Id ?? (kind == "lighthouse" ? "ex_2cvvUZQhDyeJ" : isTeam ? "CloudOpsTeam" : "ContentCreator");
                var expertName = expert?.Name ?? (kind == "lighthouse" ? "腾讯轻量云专家" : isTeam ? "运维专家团队" : "内容创作专家");
                payload["mode"] = "CLOUD";
                payload["id"] = expertId;
                payload["name"] = expertName;
                payload["expertTitle"] = expertName;
                payload["type"] = "02-Engineering";
                payload["expertType"] = isTeam ? "team" : "agent";
                payload["source"] = "builtin";
                payload["version"] = "1.0.2";
                payload["cost"] = 0;
                payload["characterCount"] = 12;
                payload["conversationId"] = conversationId;
                payload["requestId"] = requestId;
                payload["messageId"] = requestId;
                payload["requestModelId"] = "deepseek-v4-flash";
                payload["requestModelName"] = "DeepSeek V4 Flash";
                break;
            case "skill":
                payload["skillId"] = "skill_2096525080079265792";
                payload["name"] = "pptx";
                break;
            case "automation":
                payload["name"] = "每周工作整理";
                payload["type"] = "cron";
                payload["source"] = "manually";
                payload["modelId"] = "deepseek-v4-flash";
                payload["modelIsThinking"] = false;
                payload["conversationId"] = conversationId;
                payload["requestId"] = requestId;
                payload["schedule"] = new Dictionary<string, object?>
                {
                    ["type"] = "recurring",
                    ["rrule"] = "FREQ=WEEKLY;BYDAY=FR;BYHOUR=9;BYMINUTE=0"
                };
                payload["prompt"] = "每周五自动整理本周工作";
                break;
            case "playbook":
                payload["id"] = "worker-ledger-freedom-dashboard";
                payload["name"] = "打工人小账本";
                payload["type"] = "other";
                payload["promptLength"] = 10;
                payload["isOfficial"] = 1;
                payload["source"] = "discover";
                payload["conversationId"] = conversationId;
                payload["requestId"] = requestId;
                break;
            case "skin":
                payload["action"] = "apply";
                payload["source"] = "settings_close";
                payload["id"] = "theme-tkmw7j";
                payload["vipLevel"] = "free";
                payload["series"] = "craft";
                payload["type"] = "unknown";
                payload["name"] = "和平精英激战金秋";
                break;
            case "chat":
            case "glmchat":
            case "cat":
                var glm = kind is "glmchat" or "cat";
                payload["mode"] = kind == "cat" ? "night" : "craft";
                payload["conversationId"] = conversationId;
                payload["requestId"] = requestId;
                payload["inputLength"] = 12;
                payload["requestModelId"] = glm ? "glm-5.2" : "deepseek-v4-flash";
                payload["requestModelName"] = glm ? "GLM-5.2" : "DeepSeek V4 Flash";
                payload["isPlan"] = false;
                payload["agentName"] = "default";
                payload["agentType"] = "conversation";
                break;
        }
        return payload;
    }

    private static bool InNightWindow(DateTimeOffset now) => now.Hour >= 23 || now.Hour < 8;
    private bool InNightWindow() => InNightWindow(Clock());

    private void Record(string task, string trigger, bool ok, string message, string? token)
    {
        var row = new Dictionary<string, object?>
        {
            ["ts"] = Clock().ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["uid"] = string.IsNullOrEmpty(token) ? "" : WorkBuddyAccount.Uid(token),
            ["realm"] = account.International ? "intl" : "cn",
            ["task"] = task,
            ["trigger"] = trigger,
            ["ok"] = ok,
            ["message"] = SafeMessage(message)
        };
        try
        {
            lock (SharedHistoryGate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(HistoryPath, JsonSerializer.Serialize(row) + Environment.NewLine);
                var info = new FileInfo(HistoryPath);
                if (info.Length > 2_000_000)
                {
                    var lines = File.ReadAllLines(HistoryPath);
                    if (lines.Length > 5000) File.WriteAllLines(HistoryPath, lines[^5000..]);
                }
            }
        }
        catch { }
    }

    private void AddLog(string message)
    {
        lock (logGate)
        {
            logs.Add("[" + Clock().ToString("HH:mm:ss") + "] " + SafeMessage(message));
            if (logs.Count > 60) logs.RemoveRange(0, logs.Count - 60);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }

    private string LocalStamp() => Clock().ToString("yyyy-MM-dd HH:mm:ss");
    private bool DoneToday(string? value) => !string.IsNullOrEmpty(value) && value.StartsWith(Clock().ToString("yyyy-MM-dd"), StringComparison.Ordinal);

    private AutomationState LoadState()
    {
        lock (SharedStateGate)
        {
            try
            {
                if (!File.Exists(StatePath)) return new();
                return JsonSerializer.Deserialize<AutomationState>(File.ReadAllText(StatePath), JsonOptions) ?? new();
            }
            catch { return new(); }
        }
    }

    private void Mark(Action<AutomationState> change)
    {
        lock (SharedStateGate)
        {
            AutomationState state;
            try { state = File.Exists(StatePath) ? JsonSerializer.Deserialize<AutomationState>(File.ReadAllText(StatePath), JsonOptions) ?? new() : new(); }
            catch { state = new(); }
            change(state);
            try
            {
                Directory.CreateDirectory(directory);
                var temp = StatePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temp, StatePath, true);
            }
            catch { }
        }
    }

    private AutomationSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AutomationSettings().Normalize();
            var settings = JsonSerializer.Deserialize<AutomationSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AutomationSettings();
            return settings.Normalize();
        }
        catch { return new AutomationSettings().Normalize(); }
    }

    private static DateTimeOffset? ComputeNext(AutomationSettings settings, DateTimeOffset now)
    {
        foreach (var hour in settings.Hours)
        {
            var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
            if (candidate > now) return candidate;
        }
        if (settings.Hours.Length == 0) return null;
        var tomorrow = now.AddDays(1);
        return new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, settings.Hours[0], 0, 0, now.Offset);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed class AutomationSettings
    {
        public bool Enabled { get; set; } = true;
        public bool RunOnStartup { get; set; } = true;
        public bool UseWebDailyChat { get; set; } = true;
        public int[]? CheckinHours { get; set; }
        public int[]? KeepaliveHours { get; set; }
        public int[]? NightHours { get; set; }
        [JsonIgnore] public int[] Hours { get; private set; } = [9, 21, 22, 1];
        public AutomationSettings Normalize()
        {
            CheckinHours = NormalizeHours(CheckinHours, [9, 21]);
            KeepaliveHours = NormalizeHours(KeepaliveHours, [22]);
            NightHours = NormalizeHours(NightHours, [1]);
            Hours = CheckinHours.Concat(KeepaliveHours).Concat(NightHours).Distinct().OrderBy(hour => hour).ToArray();
            return this;
        }
        private static int[] NormalizeHours(int[]? hours, int[] fallback)
        {
            var normalized = (hours ?? fallback).Where(hour => hour is >= 0 and <= 23).Distinct().OrderBy(hour => hour).ToArray();
            return normalized.Length == 0 ? fallback : normalized;
        }
    }

    private sealed class AutomationState
    {
        public RealmState Cn { get; set; } = new();
        public RealmState Intl { get; set; } = new();
    }

    private sealed class RealmState
    {
        public string? LastCheckin { get; set; }
        public string? LastActivityReport { get; set; }
        public string? LastDailyChat { get; set; }
    }

    private static JsonElement DataElement(JsonElement body) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("data", out var data) ? data : default;
    private static JsonElement ObjectElement(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    private static string StringProperty(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";
    private static double Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
    private static bool MessageContains(string value, string marker) => value.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static readonly Regex[] SecretPatterns =
    [
        new(@"(?i)\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled),
        new(@"(?i)\b(?:access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|authorization|cookie|set-cookie|password|secret|token)\b\s*[:=]\s*\S+", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}\b", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9_-]{12,}\b", RegexOptions.Compiled),
        new(@"\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled),
        new(@"\b[A-Za-z0-9+/]{40,}={0,2}\b", RegexOptions.Compiled)
    ];

    internal static string SafeMessage(string? value)
    {
        var line = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var pattern in SecretPatterns) line = pattern.Replace(line, "[redacted]");
        return line.Length > 200 ? line[..200] : line;
    }

    private sealed class AcpConnection : IAsyncDisposable
    {
        private readonly HttpClient http;
        private readonly Uri link;
        private readonly string token;
        private readonly string userAgent;
        private readonly CancellationTokenSource readerCancellation = new();
        private HttpResponseMessage? stream;
        private Task? reader;
        public string ConnectionId { get; private set; } = "";
        public int Chunks { get; private set; }

        private AcpConnection(HttpClient http, Uri link, string token, string userAgent)
        {
            this.http = http;
            this.link = link;
            this.token = token;
            this.userAgent = userAgent;
        }

        public static async Task<AcpConnection> OpenAsync(HttpClient http, Uri link, string token, string userAgent, CancellationToken ct)
        {
            var connection = new AcpConnection(http, link, token, userAgent);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, link);
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                connection.stream = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!connection.stream.IsSuccessStatusCode) throw new InvalidOperationException("SSE 通道返回 HTTP " + (int)connection.stream.StatusCode);
                connection.ConnectionId = connection.stream.Headers.TryGetValues("Acp-Connection-Id", out var values) ? values.FirstOrDefault() ?? "" : "";
                if (string.IsNullOrEmpty(connection.ConnectionId)) throw new InvalidOperationException("SSE 通道没有返回 Acp-Connection-Id");
                connection.reader = Task.Run(() => connection.ReadAsync(connection.stream, connection.readerCancellation.Token));
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }

        private async Task ReadAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(body, Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var payload = line[5..].Trim();
                    if (payload.Length == 0 || payload == "[DONE]") continue;
                    try
                    {
                        using var document = JsonDocument.Parse(payload);
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) continue;
                        if (StringProperty(root, "method") != "session/update") continue;
                        var parameters = ObjectElement(root, "params");
                        var update = ObjectElement(parameters, "update");
                        if (StringProperty(update, "sessionUpdate") == "agent_message_chunk") Chunks++;
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        public async Task RequestAsync(string method, object parameters, int id, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, link);
            request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            }), Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("Acp-Connection-Id", ConnectionId);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using var response = await http.SendAsync(request, ct);
            await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode is not (200 or 202)) throw new InvalidOperationException(method + " 返回 HTTP " + (int)response.StatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            readerCancellation.Cancel();
            stream?.Dispose();
            if (reader != null) { try { await reader; } catch { } }
            readerCancellation.Dispose();
        }
    }
}
