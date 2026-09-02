using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

// Rewrite, not a port, of Aetherphone's WatchAlongSession networking half - that one is built
// directly on AethernetSession/CallHub (account auth, a websocket shared with phone calls). This
// talks to AlphaChannel.Server's dedicated /rt endpoint instead, with the same stream.* message
// shape (see AlphaChannel.Contracts). Auth is now a real XIVAuth-backed account (see Auth/) - the
// bearer token comes from the current character's CharacterSession, not a self-asserted UserId.
internal enum StreamMode
{
    None,
    Hosting,
    Viewing,
}

internal sealed class StreamClient : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private readonly Func<string?> displayNameProvider;
    private readonly Func<CharacterSession?> sessionProvider;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private ClientWebSocket? socket;
    private Task? runTask;

    // Snapshot of sessionProvider().AccountId taken at the moment a connection is established -
    // used for the rest of that connection's lifetime so a mid-session character switch can't
    // desync self-identity checks (e.g. the host-transfer comparison in Dispatch) from what the
    // server actually authenticated this socket as.
    private string? myAccountId;
    private StreamControl? lastHostState;
    private string? pendingJoinHost;
    private string? pendingJoinPassword;

    internal StreamMode Mode { get; private set; } = StreamMode.None;
    internal string? HostId { get; private set; }
    internal ParticipantInfo[] Roster { get; private set; } = [];
    internal bool IsConnected => socket?.State == WebSocketState.Open;

    // Filled from Dispatch (a background thread) - drained by MainWindow's Draw (main thread) each
    // frame, so this needs to be thread-safe for that handoff, same reasoning as everywhere else
    // in this plugin that crosses from the receive loop back to the main thread.
    internal ConcurrentQueue<(
        string UserId,
        string DisplayName,
        string Glyph)> IncomingReactions
    { get; } = new();

    internal ConcurrentQueue<(
        string UserId,
        string DisplayName,
        string Text)> IncomingChat
    { get; } = new();

    internal ConcurrentQueue<(
        string DisplayName,
        Guid RequestId,
        string Url,
        string Title,
        string Source,
        TimeSpan? Duration,
        string? ThumbnailUrl)> IncomingMediaRequests
    { get; } = new();

    internal ConcurrentQueue<(
    Guid RequestId,
    bool PlayNow,
    int QueuePosition)> IncomingMediaRequestResults
    { get; } = new();

    internal event Action<StreamControl>? OnState;
    internal event Action? OnJoined;
    internal event Action<string?>? OnDeclined;
    internal event Action? OnEnded;

    // Fired when an admin reset this user's name server-side - the plugin should prompt for a new
    // one again, same as the first-connect flow.
    internal event Action? OnRenameRequired;

    // friend.*/presence.*/dm.*/activity.* pushes - see AlphaChannel.Contracts.SocialControl. These
    // are push-only (the server never expects a SocialControl back), so unlike the stream.* events
    // above there's no corresponding SendXAsync method for most of them - mutations go over REST
    // (Social/DmService/etc. clients, added alongside the features that use them).
    internal event Action<SocialControl>? OnFriendRequestReceived;
    internal event Action<SocialControl>? OnFriendAccepted;
    internal event Action<SocialControl>? OnFriendRemoved;
    internal event Action<SocialControl>? OnPresenceUpdate;
    internal event Action<int>? OnOnlineCount;
    internal event Action<SocialControl>? OnDmMessage;
    internal event Action<SocialControl>? OnActivityNew;

    internal StreamClient(Configuration configuration, Func<string?> displayNameProvider, Func<CharacterSession?> sessionProvider)
    {
        this.configuration = configuration;
        this.displayNameProvider = displayNameProvider;
        this.sessionProvider = sessionProvider;
    }

    internal void Start()
    {
        runTask = Task.Run(() => RunAsync(lifetime.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var session = sessionProvider();
            if (session is null)
            {
                // No signed-in account for the current character - nothing to connect with.
                // Player/Screen/Settings don't need this, so we just idle rather than error.
                await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Bearer {session.Token}");
                await ws.ConnectAsync(BuildUri(configuration.RelayServerUrl), token).ConfigureAwait(false);
                socket = ws;
                myAccountId = session.AccountId;
                AepLog.Info("[Stream] connected");
                if (displayNameProvider() is { Length: > 0 } name)
                {
                    await SendHelloAsync(name).ConfigureAwait(false);
                }

                await FlushPendingAsync(token).ConfigureAwait(false);

                await ReceiveLoopAsync(ws, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Stream] connection error: {exception.Message}");
            }
            finally
            {
                socket = null;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var stream = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var bytes = stream.ToArray();
            string? type;
            try
            {
                using var peek = JsonDocument.Parse(bytes);
                type = peek.RootElement.TryGetProperty("Type", out var typeEl) ? typeEl.GetString() : null;
            }
            catch (JsonException exception)
            {
                AepLog.Warning($"[Stream] malformed message: {exception.Message}");
                continue;
            }

            // friend./presence./dm./activity. share the socket with stream. but are a distinct
            // envelope shape (SocialControl, not StreamControl) - see AlphaChannel.Contracts.
            var isSocial = type is not null && (type.StartsWith("friend.", StringComparison.Ordinal) ||
                type.StartsWith("presence.", StringComparison.Ordinal) ||
                type.StartsWith("dm.", StringComparison.Ordinal) ||
                type.StartsWith("activity.", StringComparison.Ordinal));

            try
            {
                if (isSocial)
                {
                    if (JsonSerializer.Deserialize<SocialControl>(bytes) is { } social)
                    {
                        DispatchSocial(social);
                    }
                }
                else if (JsonSerializer.Deserialize<StreamControl>(bytes) is { } message)
                {
                    Dispatch(message);
                }
            }
            catch (JsonException exception)
            {
                AepLog.Warning($"[Stream] malformed message: {exception.Message}");
            }
        }
    }

    private void Dispatch(StreamControl message)
    {
        switch (message.Type)
        {
            case SignalType.StreamState:

                OnState?.Invoke(message);
                break;
            case SignalType.StreamJoined:
                Mode = StreamMode.Viewing;
                // Echoed back as the host's real UserId (JoinAsync sent their display name) - keep
                // it, in case anything ever needs the resolved identity rather than the typed name.
                if (message.HostId is { Length: > 0 } resolvedHostId)
                {
                    HostId = resolvedHostId;
                }

                OnJoined?.Invoke();
                break;
            case SignalType.StreamDeclined:
                Mode = StreamMode.None;
                pendingJoinHost = null;
                pendingJoinPassword = null;
                OnDeclined?.Invoke(message.Reason);
                break;
            case SignalType.StreamRoster:
                Roster = message.Participants ?? [];
                break;
            case SignalType.StreamEnded:
                Mode = StreamMode.None;
                HostId = null;
                lastHostState = null;
                pendingJoinHost = null;
                pendingJoinPassword = null;

                IncomingChat.Clear();
                IncomingMediaRequests.Clear();

                OnEnded?.Invoke();
                break;

            case SignalType.StreamRenameRequired:
                OnRenameRequired?.Invoke();
                break;

            case SignalType.StreamHostTransferred:
                if (message.HostId == myAccountId)
                {
                    Mode = StreamMode.Hosting;
                    HostId = null;
                }
                else
                {
                    Mode = StreamMode.Viewing;
                    HostId = message.HostId;
                }

                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } glyph &&
                    message.UserId is { Length: > 0 } senderId:
                {
                    string displayName;

                    // ---------------------------------------------------------
                    // Our own reaction
                    // ---------------------------------------------------------
                    //
                    // The host is not necessarily present in Roster, so resolve
                    // our own account directly from the local display-name
                    // provider first.
                    // ---------------------------------------------------------

                    if (string.Equals(
                            senderId,
                            myAccountId,
                            StringComparison.Ordinal))
                    {
                        displayName =
                            displayNameProvider() ??
                            "You";
                    }
                    else
                    {
                        // -----------------------------------------------------
                        // Another party member
                        // -----------------------------------------------------
                        //
                        // The roster already maps the relay UserId to the
                        // friendly Alpha Channel display name.
                        // -----------------------------------------------------

                        var participant =
                            Roster.FirstOrDefault(
                                member =>
                                    string.Equals(
                                        member.UserId,
                                        senderId,
                                        StringComparison.Ordinal));

                        displayName =
                            participant?.DisplayName ??
                            message.DisplayName ??
                            "Someone";
                    }

                    IncomingReactions.Enqueue(
                        (
                            senderId,
                            displayName,
                            glyph
                        ));

                    break;
                }

            case SignalType.StreamChat when message.ChatText is { Length: > 0 } text:
                {
                    var displayName =
                        message.DisplayName ??
                        message.UserId ??
                        "Someone";

                    const string mediaRequestPrefix =
       "[[AC_MEDIA_REQUEST_1]]";

                    const string mediaResultPrefix =
    "[[AC_MEDIA_RESULT_1]]";

                    if (text.StartsWith(
        mediaResultPrefix,
        StringComparison.Ordinal))
                    {
                        var payload =
                            text[mediaResultPrefix.Length..];

                        var parts =
                            payload.Split('|');

                        if (parts.Length != 3 ||
                            !Guid.TryParseExact(
                                parts[0],
                                "N",
                                out var requestId) ||
                            !int.TryParse(
                                parts[2],
                                out var queuePosition))
                        {
                            AepLog.Warning(
                                "[Stream] Ignored malformed media request result.");

                            break;
                        }

                        var playNow =
                            parts[1] == "play";

                        if (!playNow &&
                            parts[1] != "queue")
                        {
                            AepLog.Warning(
                                "[Stream] Ignored unknown media request result.");

                            break;
                        }

                        IncomingMediaRequestResults.Enqueue(
                            (
                                requestId,
                                playNow,
                                queuePosition
                            ));

                        break;
                    }

                    if (text.StartsWith(
               mediaRequestPrefix,
               StringComparison.Ordinal))
                    {
                        var payload =
                            text[mediaRequestPrefix.Length..];

                        var separatorIndex =
                            payload.IndexOf('|');

                        if (separatorIndex <= 0)
                        {
                            AepLog.Warning(
                                "[Stream] Ignored malformed media request.");

                            break;
                        }

                        var requestIdText =
                            payload[..separatorIndex];

                        var url =
                            payload[(separatorIndex + 1)..];

                        if (!Guid.TryParseExact(
                                requestIdText,
                                "N",
                                out var requestId) ||
                            string.IsNullOrWhiteSpace(url))
                        {
                            AepLog.Warning(
                                "[Stream] Ignored malformed media request.");

                            break;
                        }

                        IncomingMediaRequests.Enqueue(
                            (
                                displayName,
                                requestId,
                                url,
                                url,
                                string.Empty,
                                null,
                                null
                            ));

                        break;
                    }

                    IncomingChat.Enqueue(
                        (
                            message.UserId ?? string.Empty,
                            displayName,
                            text
                        ));

                    break;
                }
        }
    }

    private void DispatchSocial(SocialControl message)
    {
        switch (message.Type)
        {
            case SocialSignalType.FriendRequestReceived:
                OnFriendRequestReceived?.Invoke(message);
                break;
            case SocialSignalType.FriendAccepted:
                OnFriendAccepted?.Invoke(message);
                break;
            case SocialSignalType.FriendRemoved:
                OnFriendRemoved?.Invoke(message);
                break;
            case SocialSignalType.PresenceUpdate:
                OnPresenceUpdate?.Invoke(message);
                break;
            case SocialSignalType.OnlineCount when message.OnlineCount is { } count:
                OnOnlineCount?.Invoke(count);
                break;
            case SocialSignalType.DmMessage:
                OnDmMessage?.Invoke(message);
                break;
            case SocialSignalType.ActivityNew:
                OnActivityNew?.Invoke(message);
                break;
        }
    }

    internal Task SendHelloAsync(string displayName) =>
        SendAsync(new StreamControl { Type = SignalType.StreamHello, DisplayName = displayName });

    internal bool IsPrivate { get; set; }
    internal string? RoomDescription { get; set; }
    internal string? RoomLocation { get; set; }
    internal RoomKind RoomKind { get; set; } = RoomKind.Public;
    internal string? RoomPassword { get; set; }

    internal Task PublishStateAsync(string? url, double positionSeconds, bool paused, Vector3? screenPosition,
        float? screenYaw, float? screenScale)
    {
        Mode = StreamMode.Hosting;
        pendingJoinHost = null;
        pendingJoinPassword = null;
        lastHostState = new StreamControl
        {
            Type = SignalType.StreamState,
            HostId = myAccountId,
            Url = url,
            PositionSeconds = positionSeconds,
            Paused = paused,
            ScreenX = screenPosition?.X,
            ScreenY = screenPosition?.Y,
            ScreenZ = screenPosition?.Z,
            ScreenYaw = screenYaw,
            ScreenScale = screenScale,
            IsPrivate = IsPrivate,
            Description = RoomDescription ?? "",
            Location = RoomLocation ?? "",
            Kind = RoomKind,
            Password = RoomKind == RoomKind.Locked ? RoomPassword : null,
        };
        return SendAsync(lastHostState);
    }

    internal Task JoinAsync(string hostId, string? password = null)
    {
        HostId = hostId;
        pendingJoinHost = hostId;
        pendingJoinPassword = string.IsNullOrWhiteSpace(password) ? null : password.Trim();
        lastHostState = null;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamJoin,
            HostId = hostId,
            Password = pendingJoinPassword,
        });
    }

    // targetUserId comes straight from Roster (ParticipantInfo.UserId) - the host already has real
    // UserIds for everyone currently watching, no name lookup needed like JoinAsync's does.
    internal Task TransferHostAsync(string targetUserId) =>
        SendAsync(new StreamControl { Type = SignalType.StreamTransferHost, HostId = targetUserId });

    internal Task SendReactionAsync(string glyph) =>
        SendAsync(new StreamControl { Type = SignalType.StreamReaction, Reaction = glyph });

    internal Task SendChatAsync(string text) =>
        SendAsync(new StreamControl
        {
            Type = SignalType.StreamChat,
            ChatText = text
        });



    internal Task SendMediaRequestAsync(
      string url,
      string title,
      string source,
      TimeSpan? duration,
      string? thumbnailUrl)
    {
        const string mediaRequestPrefix =
            "[[AC_MEDIA_REQUEST_1]]";

        var requestId =
            Guid.NewGuid();

        return SendAsync(
            new StreamControl
            {
                Type = SignalType.StreamChat,
                ChatText =
                    mediaRequestPrefix +
                    requestId.ToString("N") +
                    "|" +
                    url
            });
    }

    internal Task SendMediaRequestResultAsync(
    Guid requestId,
    bool playNow,
    int queuePosition)
    {
        const string mediaResultPrefix =
            "[[AC_MEDIA_RESULT_1]]";

        return SendAsync(
            new StreamControl
            {
                Type = SignalType.StreamChat,
                ChatText =
                    mediaResultPrefix +
                    requestId.ToString("N") +
                    "|" +
                    (playNow
                        ? "play"
                        : "queue") +
                    "|" +
                    queuePosition
            });
    }

    internal async Task LeaveAsync()
    {
        if (Mode == StreamMode.None)
        {
            return;
        }

        await SendAsync(new StreamControl { Type = SignalType.StreamLeave, HostId = HostId }).ConfigureAwait(false);
        Mode = StreamMode.None;
        HostId = null;
        Roster = [];
        lastHostState = null;
        pendingJoinHost = null;
        pendingJoinPassword = null;

        IncomingChat.Clear();
        IncomingMediaRequests.Clear();
    }

    private async Task FlushPendingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (lastHostState is { } hostState)
        {
            await SendAsync(hostState with { HostId = myAccountId }).ConfigureAwait(false);
            return;
        }

        if (pendingJoinHost is { Length: > 0 } host)
        {
            await SendAsync(new StreamControl
            {
                Type = SignalType.StreamJoin,
                HostId = host,
                Password = pendingJoinPassword,
            }).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(StreamControl message)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
        {
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ws.SendAsync(json, WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[Stream] send failed: {exception.Message}");
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static Uri BuildUri(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed["https://".Length..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed["http://".Length..];
        }

        return new Uri(trimmed + "/rt");
    }

    public void Dispose()
    {
        lifetime.Cancel();
        socket?.Dispose();
        lifetime.Dispose();
        sendLock.Dispose();
    }
}
