using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080")));
var app = builder.Build();

app.UseWebSockets();

var rooms = new ConcurrentDictionary<string, Room>();

app.MapGet("/", () => "AlphaChannel relay is running.");

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await HandleConnectionAsync(socket, rooms, context.RequestAborted);
});

app.Run();

static async Task HandleConnectionAsync(WebSocket socket, ConcurrentDictionary<string, Room> rooms, CancellationToken ct)
{
    string? ownedRoomCode = null;
    bool isJoinerInRoom = false;

    try
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string text = Encoding.UTF8.GetString(ms.ToArray());
            RelayMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<RelayMessage>(text);
            }
            catch (JsonException)
            {
                continue;
            }
            if (msg == null) { continue; }

            switch (msg.Type)
            {
                case "host":
                {
                    string code = GenerateUniqueCode(rooms);
                    var room = new Room { Host = socket };
                    rooms[code] = room;
                    ownedRoomCode = code;
                    await SendAsync(socket, new RelayMessage { Type = "hosted", Code = code }, ct);
                    break;
                }
                case "join":
                {
                    if (msg.Code != null && rooms.TryGetValue(msg.Code, out Room? room)
                        && Interlocked.CompareExchange(ref room.Joiner, socket, null) == null)
                    {
                        ownedRoomCode = msg.Code;
                        isJoinerInRoom = true;
                        await SendAsync(socket, new RelayMessage { Type = "paired" }, ct);
                        await SendAsync(room.Host, new RelayMessage { Type = "paired" }, ct);
                    }
                    else
                    {
                        await SendAsync(socket, new RelayMessage { Type = "error", Message = "Room not found or already full" }, ct);
                    }
                    break;
                }
                case "input":
                {
                    if (ownedRoomCode != null && rooms.TryGetValue(ownedRoomCode, out Room? room))
                    {
                        WebSocket? peer = isJoinerInRoom ? room.Host : room.Joiner;
                        if (peer is { State: WebSocketState.Open })
                        {
                            await SendAsync(peer, msg, ct);
                        }
                    }
                    break;
                }
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    finally
    {
        if (ownedRoomCode != null && rooms.TryGetValue(ownedRoomCode, out Room? room))
        {
            WebSocket? peer = isJoinerInRoom ? room.Host : room.Joiner;
            if (peer is { State: WebSocketState.Open })
            {
                try { await SendAsync(peer, new RelayMessage { Type = "peer_left" }, CancellationToken.None); }
                catch { /* best effort */ }
            }

            if (!isJoinerInRoom)
            {
                rooms.TryRemove(ownedRoomCode, out _);
            }
            else
            {
                room.Joiner = null; //free the slot so someone else can join with the same code
            }
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { /* best effort */ }
        }
    }
}

static async Task SendAsync(WebSocket socket, RelayMessage msg, CancellationToken ct)
{
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
}

static string GenerateUniqueCode(ConcurrentDictionary<string, Room> rooms)
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; //no 0/O/1/I to avoid ambiguity
    var rng = Random.Shared;
    string code;
    do
    {
        code = new string(Enumerable.Range(0, 6).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
    } while (rooms.ContainsKey(code));
    return code;
}

sealed class Room
{
    public required WebSocket Host { get; init; }
    public WebSocket? Joiner;
}

sealed class RelayMessage
{
    public string Type { get; set; } = "";
    public string? Code { get; set; }
    public string? Message { get; set; }
    public int? Port { get; set; }
    public int? Id { get; set; }
    public bool? Pressed { get; set; }
}
