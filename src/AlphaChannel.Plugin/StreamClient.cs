using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

// Connects to the Alpha Channel server's /rt WebSocket endpoint using AlphaChannel.Contracts.
// Authenticates with the bearer token from the current character's CharacterSession.
internal enum StreamMode
{
    None,
    Hosting,
    Viewing,
}

internal enum RealtimeConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    AuthenticationRequired,
    ServerUnavailable,
}

internal sealed record PartyNextQueueSnapshot(
    int Count,
    string? Title,
    string? Source,
    double? DurationSeconds,
    string? ThumbnailUrl);

internal sealed class StreamClient : IDisposable
{
    private static readonly TimeSpan SignedOutPollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly Configuration configuration;
    private readonly Func<string?> displayNameProvider;
    private readonly Func<CharacterSession?> sessionProvider;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly object lifecycleGate = new();
    private readonly ManualResetEventSlim sendsDrained = new(true);
    private ClientWebSocket? socket;
    private Task? runTask;
    private bool disposed;
    private int activeSends;
    private volatile RealtimeConnectionState connectionState =
        RealtimeConnectionState.Disconnected;
    private int connectionGeneration;
    private string? rejectedSessionToken;

    // Snapshot of sessionProvider().AccountId taken at the moment a connection is established -
    // used for the rest of that connection's lifetime so a mid-session character switch can't
    // desync self-identity checks (e.g. the host-transfer comparison in Dispatch) from what the
    // server actually authenticated this socket as.
    private string? myAccountId;
    private StreamControl? lastHostState;
    private string? pendingJoinHost;
    private string? pendingJoinPassword;

    //
    // Prevent periodic stream.state publication from recreating a room
    // while its host's stream.leave message is being sent.
    //
    private volatile bool leavingRoom;

    internal StreamMode Mode { get; private set; } = StreamMode.None;
    internal string? HostId { get; private set; }
    internal ParticipantInfo[] Roster { get; private set; } = [];
    internal bool IsConnected => socket?.State == WebSocketState.Open;
    internal RealtimeConnectionState ConnectionState => connectionState;
    internal string ConnectionStatusText => connectionState switch
    {
        RealtimeConnectionState.Connecting => "Connecting",
        RealtimeConnectionState.Connected => "Connected",
        RealtimeConnectionState.Reconnecting => "Reconnecting",
        RealtimeConnectionState.AuthenticationRequired => "Authentication required",
        RealtimeConnectionState.ServerUnavailable => "Server unavailable",
        _ => "Disconnected",
    };

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
        string UserId,
        string DisplayName,
        Guid RequestId,
        string Url,
        string Title,
        string Source,
        TimeSpan? Duration,
        string? ThumbnailUrl)> IncomingMediaRequests
    { get; } = new();

    internal ConcurrentQueue<(
        string UserId,
        string DisplayName,
        Guid RequestId,
        string Url)> PendingHostMediaRequests { get; } = new();

    private readonly ConcurrentDictionary<Guid, string> observedMediaRequestUrls = new();
    internal ConcurrentQueue<PartyNextQueueSnapshot> IncomingNextQueueSnapshots { get; } = new();
    private readonly Dictionary<string, string?[]> incomingNextQueueChunks = new(StringComparer.Ordinal);

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
        if (disposed)
        {
            return;
        }

        runTask = Task.Run(() => RunAsync(lifetime.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        var reconnectAttempt = 0;
        var hasConnected = false;

        while (!token.IsCancellationRequested)
        {
            var session = sessionProvider();
            if (session is null)
            {
                SetConnectionState(RealtimeConnectionState.AuthenticationRequired);
                ClearRoomForAuthenticationLoss();
                rejectedSessionToken = null;
                reconnectAttempt = 0;
                await DelaySafelyAsync(SignedOutPollDelay, token).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(
                    rejectedSessionToken,
                    session.Token,
                    StringComparison.Ordinal))
            {
                SetConnectionState(RealtimeConnectionState.AuthenticationRequired);
                await DelaySafelyAsync(SignedOutPollDelay, token).ConfigureAwait(false);
                continue;
            }

            SetConnectionState(
                hasConnected || reconnectAttempt > 0
                    ? RealtimeConnectionState.Reconnecting
                    : RealtimeConnectionState.Connecting);

            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Bearer {session.Token}");

                using var connection =
                    CancellationTokenSource.CreateLinkedTokenSource(token);
                connection.CancelAfter(ConnectTimeout);

                await ws.ConnectAsync(
                        BuildUri(configuration.RelayServerUrl),
                        connection.Token)
                    .ConfigureAwait(false);

                var generation = Interlocked.Increment(ref connectionGeneration);
                socket = ws;
                myAccountId = session.AccountId;
                hasConnected = true;
                reconnectAttempt = 0;
                rejectedSessionToken = null;
                SetConnectionState(RealtimeConnectionState.Connected);
                AepLog.Info("[Realtime] Connected.");

                if (displayNameProvider() is { Length: > 0 } name)
                {
                    await SendHelloAsync(name).ConfigureAwait(false);
                }

                await FlushPendingAsync(token).ConfigureAwait(false);

                using var activeConnection =
                    CancellationTokenSource.CreateLinkedTokenSource(token);

                var receiveTask =
                    ReceiveLoopAsync(ws, generation, activeConnection.Token);
                var sessionMonitorTask =
                    MonitorSessionAsync(session, activeConnection.Token);

                await Task.WhenAny(receiveTask, sessionMonitorTask)
                    .ConfigureAwait(false);

                activeConnection.Cancel();
                try
                {
                    ws.Abort();
                }
                catch
                {
                    // The receive loop may already have closed the socket.
                }

                try
                {
                    await Task.WhenAll(receiveTask, sessionMonitorTask)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (activeConnection.IsCancellationRequested)
                {
                    // The companion task is intentionally cancelled after the
                    // socket closes or the active character/session changes.
                }

                var currentSession = sessionProvider();
                if (currentSession is null ||
                    !string.Equals(
                        currentSession.AccountId,
                        session.AccountId,
                        StringComparison.Ordinal))
                {
                    ClearRoomForAuthenticationLoss();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException exception)
            {
                AepLog.Warning(
                    $"[Realtime] Connection timed out: {exception.Message}");
                SetConnectionState(RealtimeConnectionState.ServerUnavailable);
            }
            catch (Exception exception)
            {
                if (IsAuthenticationRejected(exception))
                {
                    rejectedSessionToken = session.Token;
                    SetConnectionState(RealtimeConnectionState.AuthenticationRequired);
                    AepLog.Warning(
                        "[Realtime] Authentication rejected. Reconnection paused until the session changes.");
                    continue;
                }

                SetConnectionState(RealtimeConnectionState.ServerUnavailable);
                AepLog.Warning($"[Realtime] Connection error: {exception.Message}");
            }
            finally
            {
                socket = null;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            reconnectAttempt++;
            var delay = GetReconnectDelay(reconnectAttempt);
            SetConnectionState(RealtimeConnectionState.Reconnecting);
            AepLog.Warning(
                $"[Realtime] Connection lost. Reconnecting in {delay.TotalSeconds:0.0} seconds " +
                $"(attempt {reconnectAttempt}).");
            await DelaySafelyAsync(delay, token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        int generation,
        CancellationToken token)
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
                        if (generation == Volatile.Read(ref connectionGeneration))
                        {
                            DispatchSocial(social);
                        }
                    }
                }
                else if (JsonSerializer.Deserialize<StreamControl>(bytes) is { } message)
                {
                    if (generation == Volatile.Read(ref connectionGeneration))
                    {
                        Dispatch(message);
                    }
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
                //
                // Retain the complete current room state so the Watch
                // Party details tab can render the same information for
                // hosts and viewers.
                //
                CurrentRoomState =
                    message;

                RoomDescription =
                    message.Description ?? "";

                RoomLocation =
                    message.Location ?? "";

                if (message.Kind is { } roomKind)
                {
                    RoomKind =
                        roomKind;
                }

                if (message.IsPrivate is { } isPrivate)
                {
                    IsPrivate =
                        isPrivate;
                }

                //
                // Password is deliberately not assigned here. The server
                // sanitizes it before broadcasting stream.state.
                //
                OnState?.Invoke(
                    message);

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
                PendingHostMediaRequests.Clear();
                observedMediaRequestUrls.Clear();
                IncomingNextQueueSnapshots.Clear();
                incomingNextQueueChunks.Clear();

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

                    const string mediaPublishedPrefix =
                        "[[AC_MEDIA_PUBLISHED_1]]";

                    const string mediaDeniedPrefix =
                        "[[AC_MEDIA_DENIED_1]]";

                    const string nextQueuePrefix = "[[AC_NEXT_QUEUE_1]]";

                    var sentByHost = Mode == StreamMode.Hosting ||
                        string.Equals(message.UserId, HostId, StringComparison.Ordinal);

                    if (text.StartsWith(nextQueuePrefix, StringComparison.Ordinal))
                    {
                        var parts = text[nextQueuePrefix.Length..].Split('|', 4);
                        if (sentByHost && parts.Length == 4 &&
                            int.TryParse(parts[1], out var chunkIndex) &&
                            int.TryParse(parts[2], out var chunkCount) &&
                            chunkCount is > 0 and <= 32 && chunkIndex >= 0 && chunkIndex < chunkCount)
                        {
                            if (!incomingNextQueueChunks.TryGetValue(parts[0], out var chunks) || chunks.Length != chunkCount)
                            {
                                chunks = new string?[chunkCount];
                                incomingNextQueueChunks[parts[0]] = chunks;
                            }

                            chunks[chunkIndex] = parts[3];
                            if (chunks.All(chunk => chunk is not null))
                            {
                                incomingNextQueueChunks.Remove(parts[0]);
                                try
                                {
                                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(chunks)));
                                    var snapshot = JsonSerializer.Deserialize<PartyNextQueueSnapshot>(json);
                                    if (snapshot is not null)
                                        IncomingNextQueueSnapshots.Enqueue(snapshot);
                                }
                                catch (Exception exception) when (exception is FormatException or JsonException)
                                {
                                    AepLog.Warning("[Stream] Ignored malformed next-queue snapshot.");
                                }
                            }
                        }

                        break;
                    }

                    if (text.StartsWith(mediaDeniedPrefix, StringComparison.Ordinal))
                    {
                        var payload = text[mediaDeniedPrefix.Length..];
                        var split = payload.IndexOf('|');
                        if (sentByHost && split > 0 &&
                            string.Equals(payload[..split], myAccountId, StringComparison.Ordinal))
                        {
                            try
                            {
                                var reason = Encoding.UTF8.GetString(Convert.FromBase64String(payload[(split + 1)..]));
                                IncomingChat.Enqueue((message.UserId ?? string.Empty, "Host", reason));
                            }
                            catch (FormatException)
                            {
                                AepLog.Warning("[Stream] Ignored malformed media request denial.");
                            }
                        }

                        break;
                    }

                    if (text.StartsWith(mediaPublishedPrefix, StringComparison.Ordinal))
                    {
                        var parts = text[mediaPublishedPrefix.Length..].Split('|', 3);
                        if (sentByHost && parts.Length == 3 &&
                            Guid.TryParseExact(parts[0], "N", out var publishedId) &&
                            observedMediaRequestUrls.TryRemove(publishedId, out var publishedUrl))
                        {
                            try
                            {
                                var requesterName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                                IncomingMediaRequests.Enqueue((
                                    parts[1], requesterName, publishedId, publishedUrl, publishedUrl,
                                    string.Empty, null, null));
                            }
                            catch (FormatException)
                            {
                                AepLog.Warning("[Stream] Ignored malformed published media request.");
                            }
                        }

                        break;
                    }

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

                        observedMediaRequestUrls[requestId] = url;

                        if (Mode == StreamMode.Hosting)
                        {
                            PendingHostMediaRequests.Enqueue((
                                message.UserId ?? string.Empty,
                                displayName,
                                requestId,
                                url));
                        }

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

    //
    // Current room metadata.
    //
    // Hosts populate these before publishing. Viewers receive the same
    // values from the host's stream.state message. Password is never
    // echoed by the server and therefore remains host-only.
    //
    internal bool IsPrivate { get; set; }

    internal string? RoomDescription { get; set; }

    internal string? RoomLocation { get; set; }

    internal RoomKind RoomKind { get; set; } =
        RoomKind.Public;

    internal string? RoomPassword { get; set; }

    internal StreamControl? CurrentRoomState
    {
        get;
        private set;
    }

    //
    // Compatibility overload for existing callers that only provide one
    // fixed-ratio scale value.
    //
    internal Task PublishStateAsync(
        string? url,
        double positionSeconds,
        bool paused,
        Vector3? screenPosition,
        float? screenYaw,
        float? screenScale,
        string? mediaTitle,
        string? mediaThumbnailUrl)
    {
        return PublishStateAsync(
            url,
            positionSeconds,
            paused,
            screenPosition,
            screenYaw,
            screenScale,
            screenDisableFixedScaleRatio: false,
            screenWidthScale: screenScale,
            screenHeightScale: screenScale,
            mediaTitle,
            mediaThumbnailUrl);
    }

    internal Task PublishStateAsync(
    string? url,
    double positionSeconds,
    bool paused,
Vector3? screenPosition,
float? screenYaw,
float? screenScale,
bool? screenDisableFixedScaleRatio,
float? screenWidthScale,
float? screenHeightScale,
string? mediaTitle,
    string? mediaThumbnailUrl)
    {
        //
        // LeaveAsync changes Mode immediately, but a framework update that
        // already began may still reach this method. Do not let that stale
        // update put the client back into Hosting or recreate the room.
        //
        if (leavingRoom)
        {
            return Task.CompletedTask;
        }

        Mode =
            StreamMode.Hosting;

        pendingJoinHost =
            null;

        pendingJoinPassword =
            null;

        lastHostState =
            new StreamControl
            {
                Type =
                    SignalType.StreamState,

                HostId =
                    myAccountId,

                Url =
                    url,

                MediaTitle =
                    mediaTitle,

                MediaThumbnailUrl =
                    mediaThumbnailUrl,

                PositionSeconds =
                    positionSeconds,

                Paused =
                    paused,

                ScreenX =
                    screenPosition?.X,

                ScreenY =
                    screenPosition?.Y,

                ScreenZ =
                    screenPosition?.Z,

                ScreenYaw =
                    screenYaw,

                ScreenScale =
    screenScale,

                ScreenDisableFixedScaleRatio =
    screenDisableFixedScaleRatio,

                ScreenWidthScale =
    screenWidthScale,

                ScreenHeightScale =
    screenHeightScale,

                IsPrivate =
                    IsPrivate,

                Description =
                    RoomDescription ?? "",

                Location =
                    RoomLocation ?? "",

                Kind =
                    RoomKind,

                Password =
                    RoomKind ==
                    RoomKind.Locked
                        ? RoomPassword
                        : null,
            };

        CurrentRoomState =
            lastHostState;

        return SendAsync(
            lastHostState);
    }

    //
    // Republishes only the editable room metadata while preserving the
    // current URL, timestamp, paused state, media details and screen
    // transform from the most recently published host state.
    //
    // This updates the existing server Room and does not create a new
    // room, clear the queue or disconnect its roster.
    //
    internal Task PublishRoomDetailsAsync()
    {
        //
        // Do not allow a pending room-details update to recreate a room
        // after its host has begun leaving.
        //
        if (leavingRoom ||
            Mode != StreamMode.Hosting)
        {
            return Task.CompletedTask;
        }

        var currentState =
            lastHostState ??
            CurrentRoomState;

        if (currentState is null)
        {
            return Task.CompletedTask;
        }

        lastHostState =
            currentState with
            {
                Type =
                    SignalType.StreamState,

                HostId =
                    myAccountId,

                IsPrivate =
                    IsPrivate,

                Description =
                    RoomDescription ?? "",

                Location =
                    RoomLocation ?? "",

                Kind =
                    RoomKind,

                //
                // An empty password explicitly clears the server's old
                // password hash when changing away from Locked.
                //
                Password =
                    RoomKind ==
                    RoomKind.Locked
                        ? RoomPassword
                        : string.Empty,
            };

        CurrentRoomState =
            lastHostState;

        return SendAsync(
            lastHostState);
    }

    internal Task JoinAsync(
        string hostId,
        string? password = null)
    {
        HostId =
            hostId;

        pendingJoinHost =
            hostId;

        pendingJoinPassword =
            string.IsNullOrWhiteSpace(
                password)
                ? null
                : password.Trim();

        lastHostState =
            null;

        //
        // Prevent the previous room's details appearing briefly while
        // waiting for the newly joined host's first state message.
        //
        CurrentRoomState =
            null;

        RoomDescription =
            null;

        RoomLocation =
            null;

        RoomKind =
            RoomKind.Public;

        RoomPassword =
            null;

        IsPrivate =
            false;

        return SendAsync(
            new StreamControl
            {
                Type =
                    SignalType.StreamJoin,

                HostId =
                    hostId,

                Password =
                    pendingJoinPassword,
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
        var requestId =
            Guid.NewGuid();

        return SendAsync(
            new StreamControl
            {
                Type = SignalType.StreamChat,
                ChatText = "[[AC_MEDIA_REQUEST_1]]" + requestId.ToString("N") + "|" + url
            });
    }

    internal Task PublishMediaRequestAsync(string requesterId, string requesterName, Guid requestId) =>
        SendAsync(new StreamControl
        {
            Type = SignalType.StreamChat,
            ChatText = "[[AC_MEDIA_PUBLISHED_1]]" + requestId.ToString("N") + "|" +
                requesterId + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(requesterName)),
        });

    internal async Task SendNextQueueSnapshotAsync(PartyNextQueueSnapshot snapshot)
    {
        const int chunkSize = 180;
        var encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        var chunkCount = Math.Max(1, (encoded.Length + chunkSize - 1) / chunkSize);
        var messageId = Guid.NewGuid().ToString("N");

        for (var index = 0; index < chunkCount; index++)
        {
            var offset = index * chunkSize;
            var chunk = encoded.Substring(offset, Math.Min(chunkSize, encoded.Length - offset));
            await SendAsync(new StreamControl
            {
                Type = SignalType.StreamChat,
                ChatText = $"[[AC_NEXT_QUEUE_1]]{messageId}|{index}|{chunkCount}|{chunk}",
            }).ConfigureAwait(false);
        }
    }

    private async Task MonitorSessionAsync(
        CharacterSession connectedSession,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            var current = sessionProvider();
            if (current is null ||
                !string.Equals(current.AccountId, connectedSession.AccountId, StringComparison.Ordinal) ||
                !string.Equals(current.Token, connectedSession.Token, StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 5);
        var seconds = Math.Min(30d, Math.Pow(2d, exponent));
        var jitter = 0.8d + Random.Shared.NextDouble() * 0.4d;
        return TimeSpan.FromSeconds(Math.Max(0.5d, seconds * jitter));
    }

    private static bool IsAuthenticationRejected(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException
                {
                    StatusCode: System.Net.HttpStatusCode.Unauthorized or
                                System.Net.HttpStatusCode.Forbidden,
                })
            {
                return true;
            }
        }

        return false;
    }

    private void SetConnectionState(RealtimeConnectionState state) =>
        connectionState = state;

    private void ClearRoomForAuthenticationLoss()
    {
        if (Mode == StreamMode.None)
        {
            return;
        }

        Mode = StreamMode.None;
        HostId = null;
        Roster = [];
        lastHostState = null;
        pendingJoinHost = null;
        pendingJoinPassword = null;
        CurrentRoomState = null;
        OnEnded?.Invoke();
    }

    private static async Task DelaySafelyAsync(
        TimeSpan delay,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    internal Task SendMediaRequestDeniedAsync(string requesterId, string reason) =>
        SendAsync(new StreamControl
        {
            Type = SignalType.StreamChat,
            ChatText = "[[AC_MEDIA_DENIED_1]]" + requesterId + "|" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(reason)),
        });

    internal Task SendMediaRequestResultAsync(
    Guid requestId,
    bool playNow,
    int queuePosition)
    {
        return SendAsync(
            new StreamControl
            {
                Type = SignalType.StreamChat,
                ChatText = "[[AC_MEDIA_RESULT_1]]" + requestId.ToString("N") + "|" +
                    (playNow ? "play" : "queue") + "|" + queuePosition,
            });
    }

    internal async Task LeaveAsync()
    {
        if (Mode ==
            StreamMode.None)
        {
            return;
        }

        //
        // The currently deployed server only guarantees room teardown when
        // the host's socket disconnects. Remember whether this client was
        // hosting before clearing its local mode.
        //
        var wasHosting =
            Mode ==
            StreamMode.Hosting;

        //
        // For viewers this identifies the room being left. A host normally
        // has no HostId because the server identifies their room from their
        // authenticated account.
        //
        var leavingHostId =
            HostId;

        //
        // Prevent any periodic state publication from recreating the room
        // while departure is in progress.
        //
        leavingRoom =
            true;

        Mode =
            StreamMode.None;

        lastHostState =
            null;

        CurrentRoomState =
            null;

        pendingJoinHost =
            null;

        pendingJoinPassword =
            null;

        try
        {
            //
            // Keep sending the explicit leave message. This supports the
            // updated server handler once that version is deployed, and is
            // still the normal departure path for viewers.
            //
            await SendAsync(
                    new StreamControl
                    {
                        Type =
                            SignalType.StreamLeave,

                        HostId =
                            leavingHostId,
                    })
                .ConfigureAwait(false);

            //
            // Temporary compatibility behavior for the currently deployed
            // server: disconnect a departing host's shared /rt socket so the
            // server's existing connection-finally cleanup closes the room
            // and broadcasts stream.ended to every viewer.
            //
            // RunAsync will establish a fresh connection automatically.
            //
            if (wasHosting)
            {
                var currentSocket =
                    socket;

                if (currentSocket is
                    {
                        State:
                            WebSocketState.Open
                    })
                {
                    try
                    {
                        await currentSocket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Watch Party host left",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            $"[Stream] Graceful host-leave disconnect failed: {exception.Message}");

                        //
                        // Abort is the fallback that guarantees the receive
                        // loop exits and the server observes disconnection.
                        //
                        try
                        {
                            currentSocket.Abort();
                        }
                        catch (Exception abortException)
                        {
                            AepLog.Warning(
                                $"[Stream] Failed to abort host connection: {abortException.Message}");
                        }
                    }
                }
            }
        }
        finally
        {
            HostId =
                null;

            Roster =
                [];

            RoomDescription =
                null;

            RoomLocation =
                null;

            RoomKind =
                RoomKind.Public;

            RoomPassword =
                null;

            IsPrivate =
                false;

            IncomingChat.Clear();
            IncomingMediaRequests.Clear();
            PendingHostMediaRequests.Clear();
            observedMediaRequestUrls.Clear();
            IncomingNextQueueSnapshots.Clear();
            incomingNextQueueChunks.Clear();

            leavingRoom =
                false;
        }
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
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            activeSends++;
            sendsDrained.Reset();
        }

        var enteredSendLock =
            false;

        try
        {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
        {
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        await sendLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
        enteredSendLock = true;

            await ws.SendAsync(json, WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Normal during plugin shutdown.
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[Stream] send failed: {exception.Message}");
        }
        finally
        {
            if (enteredSendLock)
            {
                sendLock.Release();
            }

            lock (lifecycleGate)
            {
                activeSends--;
                if (activeSends == 0)
                {
                    sendsDrained.Set();
                }
            }
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
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        lifetime.Cancel();

        try { socket?.Abort(); } catch { }
        try { socket?.Dispose(); } catch { }

        try
        {
            if (runTask is { IsCompleted: false } &&
                !runTask.Wait(TimeSpan.FromSeconds(3)))
            {
                AepLog.Warning(
                    "[Realtime] Connection worker did not stop within 3 seconds.");
            }
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                $"[Realtime] Connection worker cleanup warning: {exception.Message}");
        }

        var sendsFinished =
            sendsDrained.Wait(TimeSpan.FromSeconds(3));

        if (!sendsFinished)
        {
            // SemaphoreSlim has no native handle unless AvailableWaitHandle is
            // requested. Leaving it for GC is safer than disposing it beneath
            // a send which is still unwinding.
            AepLog.Warning(
                "[Realtime] A send was still finishing during shutdown.");
        }

        runTask = null;
        lifetime.Dispose();

        if (sendsFinished)
        {
            sendLock.Dispose();
            sendsDrained.Dispose();
        }
    }
}
