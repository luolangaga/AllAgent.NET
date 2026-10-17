using AllAgent.NET.WorkBuddy;
using AllAgent.NET.Core.Services.AI;
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent", "plugins");
var directory = Directory.GetDirectories(root, "allagent.workbuddy-*").OrderByDescending(Directory.GetLastWriteTimeUtc).First();
using var account = new WorkBuddyAccount(Path.Combine(directory, "data"));
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var config = account.CreateConfiguration();
config.DefaultModel = args.FirstOrDefault() ?? "glm-5.3-flash";
config.MaxTokens = 128;
try
{
    Console.WriteLine("MODEL " + config.DefaultModel);
    await foreach (var chunk in account.ModelProvider.StreamChatAsync(new(config, [new ChatMessage("user", "Reply with OK only.")]), deadline.Token))
        Console.WriteLine(chunk.Type + ": " + (chunk.TextDelta ?? chunk.ErrorMessage ?? chunk.FinishReason));
}
catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); Environment.ExitCode = 1; }
