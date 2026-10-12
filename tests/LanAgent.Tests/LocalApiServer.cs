using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LanAgent.Tests;

internal sealed class LocalApiServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url => _app.Urls.Single();
    private LocalApiServer(WebApplication app) => _app = app;
    public static async Task<LocalApiServer> StartAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build(); app.Run(handler); await app.StartAsync(); return new(app);
    }
    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}
