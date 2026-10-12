using System;

namespace LanAgent.Core.Bridge;

/// <summary>
/// Decouples bridge event pushing from MainWindow lifetime.
/// Handlers can be registered at app startup, and the actual push
/// delegate is wired up when MainWindow creates its WebView.
/// </summary>
public class BridgeEventPusher
{
    private Action<string>? _pusher;

    public void SetPusher(Action<string> pusher) => _pusher = pusher;

    public void Push(string json) => _pusher?.Invoke(json);
}
