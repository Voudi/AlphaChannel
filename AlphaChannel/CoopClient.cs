using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;

namespace AlphaChannel;

internal sealed class CoopClient : IDisposable
{
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;

    internal bool IsConnected => _socket is { State: WebSocketState.Open };
    internal bool IsPaired { get; private set; }
    internal string? RoomCode { get; private set; }
    internal string? LastError { get; private set; }

    internal event Action<int, int, bool>? OnRemoteInput; // port, id, pressed
    internal event Action? OnPaired;
    internal event Action? OnPeerLeft;

    internal async Task HostAsync(string relayUrl)
    {
        await ConnectAsync(relayUrl);
        await SendAsync(new RelayMessage { Type = "host" });
    }

    internal async Task JoinAsync(string relayUrl, string code)
    {
        await ConnectAsync(relayUrl);
        await SendAsync(new RelayMessage { Type = "join", Code = code });
    }

    internal Task SendInputAsync(int port, int id, bool pressed)
    {
        return !IsConnected ? Task.CompletedTask : SendAsync(new RelayMessage { Type = "input", Port = port, Id = id, Pressed = pressed });
    }

    private async Task ConnectAsync(string relayUrl)
    {
        Disconnect();
        LastError = null;

        ClientWebSocket socket = new();
        CancellationTokenSource cts = new();
        _socket = socket;
        _cts = cts;

        string wsUrl = relayUrl.Trim().TrimEnd('/');
        wsUrl = wsUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + wsUrl[8..]
              : wsUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + wsUrl[7..]
              : wsUrl;

        await socket.ConnectAsync(new Uri(wsUrl + "/ws"), cts.Token);
        _ = Task.Run(() => ReceiveLoopAsync(socket, cts.Token));
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using MemoryStream ms = new();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) { return; }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                RelayMessage? msg;
                try { msg = JsonConvert.DeserializeObject<RelayMessage>(Encoding.UTF8.GetString(ms.ToArray())); }
                catch (JsonException) { continue; }
                if (msg == null) { continue; }

                switch (msg.Type)
                {
                    case "hosted":
                        RoomCode = msg.Code;
                        break;
                    case "paired":
                        IsPaired = true;
                        OnPaired?.Invoke();
                        break;
                    case "error":
                        LastError = msg.Message;
                        break;
                    case "peer_left":
                        IsPaired = false;
                        LastError = "Your co-op partner disconnected.";
                        OnPeerLeft?.Invoke();
                        Disconnect();
                        return; //Connection is torn down; nothing left to receive.
                    case "input":
                        if (msg is { Port: not null, Id: not null, Pressed: not null })
                        {
                            OnRemoteInput?.Invoke(msg.Port.Value, msg.Id.Value, msg.Pressed.Value);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception e)
        {
            Services.Log.Error($"[Coop] Receive loop error: {e}");
        }
    }

    private async Task SendAsync(RelayMessage msg)
    {
        if (_socket is not { State: WebSocketState.Open } socket) { return; }
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { } //Disconnect() can race a concurrent send from the per-frame coop-joiner poll
        catch (OperationCanceledException) { }
    }

    internal void Disconnect()
    {
        IsPaired = false;
        RoomCode = null;
        _cts?.Cancel();
        try { _socket?.Abort(); } catch { /* best effort */ }
        _socket?.Dispose();
        _socket = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Disconnect();
}

internal sealed class RelayMessage
{
    public string Type { get; set; } = "";
    public string? Code { get; set; }
    public string? Message { get; set; }
    public int? Port { get; set; }
    public int? Id { get; set; }
    public bool? Pressed { get; set; }
}
