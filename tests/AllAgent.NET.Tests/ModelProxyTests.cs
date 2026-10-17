using System.Net;
using AllAgent.NET.Core.Services.AI;
using Xunit;

namespace AllAgent.NET.Tests;

public class ModelProxyTests
{
    [Fact]
    public void ExistingProxyFollowsEnableDisableAndPortChanges()
    {
        IWebProxy? current = new WebProxy("http://127.0.0.1:8796");
        var proxy = new CurrentWindowsProxy(_ => current);
        var endpoint = new Uri("https://example.com/v1/chat/completions");
        Assert.False(proxy.IsBypassed(endpoint));
        Assert.Equal(8796, proxy.GetProxy(endpoint)!.Port);
        current = null;
        Assert.True(proxy.IsBypassed(endpoint));
        Assert.Equal(endpoint, proxy.GetProxy(endpoint));
        current = new WebProxy("http://127.0.0.1:7890");
        Assert.False(proxy.IsBypassed(endpoint));
        Assert.Equal(7890, proxy.GetProxy(endpoint)!.Port);
    }

    [Fact]
    public void ProxyRetainsLocalBypassRules()
    {
        var proxy = new CurrentWindowsProxy(_ => new WebProxy("http://127.0.0.1:8796", true));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:11434")));
    }
}
