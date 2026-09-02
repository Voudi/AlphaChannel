using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Server.Data;
using AlphaChannel.Server.Social;

namespace AlphaChannel.Server;

// One instance handles one socket's whole lifetime. viewingHostId is a local, not a field, so this
// class itself is stateless and safe to register as a DI singleton - see the plan's v1 auth note:
// userId here is just whatever the client's Authorization: Bearer header claims, no verification
// against a real identity. Room ownership itself (who's currently hosting) is NOT tracked in a
// local anymore - see the finally block's comment for why a stream.transferHost made that unsafe.
internal sealed class ConnectionHandler(
    RoomManager rooms, UserDirectory directory, PresenceService presence, ActivityService activity, ILogger<ConnectionHandler> logger)
{
    public async Task RunAsync(WebSocket socket, string userId, CancellationToken token)
    {
        string? viewingHostId = null;
        directory.Connected(userId, socket);
        await presence.NotifyAsync(userId, online: true, token).ConfigureAwait(false);
        await presence.BroadcastOnlineCountAsync(token).ConfigureAwait(false);

        try
        {
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                stream.Position = 0;
                StreamControl? message;
                try
                {
                    message = JsonSerializer.Deserialize<StreamControl>(stream);
                }
                catch (JsonException exception)
                {
                    logger.LogWarning("malformed message from {UserId}: {Message}", userId, exception.Message);
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                switch (message.Type)
                {
                    case SignalType.StreamHello when message.DisplayName is { Length: > 0 } name:
                        directory.SetDisplayName(userId, name);
                        break;

                    case SignalType.StreamState:
                        // FindRoomHostedBy first: if this user was transferred host of an existing
                        // room, their state updates THAT room, not a brand new one keyed by them.
                        var isNewRoom = rooms.FindRoomHostedBy(userId) is null;
                        var room = rooms.FindRoomHostedBy(userId) ?? rooms.GetOrCreateRoom(userId);
                        ApplyRoomMetadata(room, message);
                        room.LastState = SanitizeState(message with
                        {
                            HostId = room.HostUserId,
                            Description = room.Description,
                            Location = room.Location,
                            Kind = room.Kind,
                            IsPrivate = room.IsPrivate,
                        });
                        await BroadcastAsync(room, room.LastState, token).ConfigureAwait(false);
                        // Safe to call every tick despite StreamState's own every-tick cadence -
                        // PresenceService dedups against the last label it actually pushed.
                        await presence.NotifyAsync(userId, online: true, token).ConfigureAwait(false);

                        if (isNewRoom && !room.IsPrivate)
                        {
                            await activity.RecordAsync(Guid.Parse(userId), ActivityEventType.StartedWatching, null, token).ConfigureAwait(false);
                        }

                        break;

                    // message.HostId carries the host's typed display name here, not their real
                    // UserId - players never see or type each other's UserId, see UserDirectory.
                    case SignalType.StreamJoin when message.HostId is { Length: > 0 } hostName:
                        if (!directory.TryResolveUserId(hostName, out var resolvedHostId))
                        {
                            await SendAsync(socket, new StreamControl { Type = SignalType.StreamDeclined, Reason = "Host not found." },
                                token).ConfigureAwait(false);
                            break;
                        }

                        // Only join an existing room that has published state — do not create an
                        // empty room for someone who isn't hosting (NearbyAutoWatch walks the floor
                        // trying character names; GetOrCreateRoom here would spam ghost rooms).
                        var target = rooms.FindRoomHostedBy(resolvedHostId);
                        if (target is null)
                        {
                            await SendAsync(socket, new StreamControl
                            {
                                Type = SignalType.StreamDeclined,
                                Reason = "Host is not streaming.",
                            }, token).ConfigureAwait(false);
                            break;
                        }

                        if (target.Kind == RoomKind.Locked)
                        {
                            if (string.IsNullOrEmpty(target.PasswordHash))
                            {
                                await SendAsync(socket, new StreamControl
                                {
                                    Type = SignalType.StreamDeclined,
                                    Reason = "This room is locked.",
                                }, token).ConfigureAwait(false);
                                break;
                            }

                            if (!PasswordMatches(target.PasswordHash, message.Password))
                            {
                                await SendAsync(socket, new StreamControl
                                {
                                    Type = SignalType.StreamDeclined,
                                    Reason = "Wrong password.",
                                }, token).ConfigureAwait(false);
                                break;
                            }
                        }

                        target.Viewers[userId] = socket;
                        viewingHostId = target.RoomKey;
                        await SendAsync(socket, new StreamControl { Type = SignalType.StreamJoined, HostId = target.HostUserId },
                            token).ConfigureAwait(false);
                        if (target.LastState is { } cached)
                        {
                            await SendAsync(socket, cached, token).ConfigureAwait(false);
                        }
                        else
                        {
                            await SendAsync(socket, new StreamControl
                            {
                                Type = SignalType.StreamState,
                                HostId = target.HostUserId,
                            }, token).ConfigureAwait(false);
                        }

                        await BroadcastRosterAsync(target, token).ConfigureAwait(false);
                        await presence.NotifyAsync(userId, online: true, token).ConfigureAwait(false);

                        if (!target.IsPrivate)
                        {
                            await activity.RecordAsync(Guid.Parse(userId), ActivityEventType.JoinedWatchAlong,
                                directory.DisplayNameOrFallback(target.HostUserId), token).ConfigureAwait(false);
                        }

                        break;

                    case SignalType.StreamLeave:
                        if (viewingHostId is { } leaveRoomKey && rooms.GetRoom(leaveRoomKey) is { } leaveRoom)
                        {
                            leaveRoom.Viewers.TryRemove(userId, out _);
                            viewingHostId = null;
                            await BroadcastRosterAsync(leaveRoom, token).ConfigureAwait(false);
                            await presence.NotifyAsync(userId, online: true, token).ConfigureAwait(false);
                        }

                        break;

                    // message.HostId carries the target viewer's real UserId here - the host already
                    // has it from their own roster (ParticipantInfo.UserId), no name lookup needed.
                    case SignalType.StreamTransferHost when message.HostId is { Length: > 0 } newHostId:
                        if (rooms.FindRoomHostedBy(userId) is not { } ownedRoom ||
                            !ownedRoom.Viewers.ContainsKey(newHostId))
                        {
                            break;
                        }

                        ownedRoom.Viewers.TryRemove(newHostId, out _);
                        ownedRoom.Viewers[userId] = socket;
                        ownedRoom.HostUserId = newHostId;
                        viewingHostId = ownedRoom.RoomKey;

                        var transferred = new StreamControl { Type = SignalType.StreamHostTransferred, HostId = newHostId };
                        if (directory.TryGetSocket(newHostId, out var newHostSocket) && newHostSocket is not null)
                        {
                            await SendAsync(newHostSocket, transferred, token).ConfigureAwait(false);
                        }

                        await BroadcastAsync(ownedRoom, transferred, token).ConfigureAwait(false);
                        await SendAsync(socket, transferred, token).ConfigureAwait(false);
                        await BroadcastRosterAsync(ownedRoom, token).ConfigureAwait(false);
                        await presence.NotifyAsync(userId, online: true, token).ConfigureAwait(false);
                        await presence.NotifyAsync(newHostId, online: true, token).ConfigureAwait(false);
                        break;

                    case SignalType.StreamReaction when message.Reaction is { Length: > 0 }:
                        // Broadcast to whichever room this user is currently part of, as either
                        // host or viewer - reactions make sense from either side of a stream.
                        var reactionRoom = rooms.FindRoomHostedBy(userId) ??
                            (viewingHostId is { } currentRoomKey ? rooms.GetRoom(currentRoomKey) : null);
                        if (reactionRoom is null)
                        {
                            break;
                        }

                        var reaction = message with { UserId = userId };
                        await BroadcastAsync(reactionRoom, reaction, token).ConfigureAwait(false);
                        if (directory.TryGetSocket(reactionRoom.HostUserId, out var reactionHostSocket) &&
                            reactionHostSocket is not null)
                        {
                            await SendAsync(reactionHostSocket, reaction, token).ConfigureAwait(false);
                        }

                        break;

                    case SignalType.StreamChat when message.ChatText is { Length: > 0 }:
                        var chatRoom = rooms.FindRoomHostedBy(userId) ??
                            (viewingHostId is { } chatRoomKey ? rooms.GetRoom(chatRoomKey) : null);
                        if (chatRoom is null)
                        {
                            break;
                        }

                        var chat = message with
                        {
                            UserId = userId,
                            DisplayName = directory.DisplayNameOrFallback(userId),
                            ChatText = message.ChatText.Length > 280 ? message.ChatText[..280] : message.ChatText,
                        };
                        await BroadcastAsync(chatRoom, chat, token).ConfigureAwait(false);
                        if (directory.TryGetSocket(chatRoom.HostUserId, out var chatHostSocket) &&
                            chatHostSocket is not null)
                        {
                            await SendAsync(chatHostSocket, chat, token).ConfigureAwait(false);
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation("socket closed for {UserId}: {Message}", userId, exception.Message);
        }
        finally
        {
            directory.Disconnected(userId);

            // Computed fresh here rather than trusting a local snapshot from whenever StreamState
            // last ran - a stream.transferHost can make someone the host of a room without them
            // ever having sent StreamState themselves, so a stale local would miss that case (or
            // wrongly tear down a room after they were transferred away from hosting it).
            if (rooms.FindRoomHostedBy(userId) is { } ownRoom)
            {
                rooms.RemoveRoom(ownRoom.RoomKey);
                await BroadcastAsync(ownRoom, new StreamControl { Type = SignalType.StreamEnded, HostId = userId },
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (viewingHostId is not null && rooms.GetRoom(viewingHostId) is { } viewedRoom)
            {
                viewedRoom.Viewers.TryRemove(userId, out _);
                await BroadcastRosterAsync(viewedRoom, CancellationToken.None).ConfigureAwait(false);
            }

            // Room state above is already torn down/updated, so this correctly computes "offline"
            // (PresenceLabels won't find this userId hosting or viewing anything anymore).
            await presence.NotifyAsync(userId, online: false, CancellationToken.None).ConfigureAwait(false);
            await presence.BroadcastOnlineCountAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task BroadcastAsync(Room room, StreamControl message, CancellationToken token)
    {
        foreach (var viewer in room.Viewers.Values)
        {
            await SendAsync(viewer, message, token).ConfigureAwait(false);
        }
    }

    private async Task BroadcastRosterAsync(Room room, CancellationToken token)
    {
        var roster = new StreamControl
        {
            Type = SignalType.StreamRoster,
            HostId = room.HostUserId,
            Participants = room.Viewers.Keys.Select(id => new ParticipantInfo(id, directory.DisplayNameOrFallback(id))).ToArray(),
        };
        foreach (var viewer in room.Viewers.Values)
        {
            await SendAsync(viewer, roster, token).ConfigureAwait(false);
        }

        // The host isn't in room.Viewers (they're not watching themselves) - push it to them
        // separately so they can see who's actually tuned in.
        if (directory.TryGetSocket(room.HostUserId, out var hostSocket) && hostSocket is not null)
        {
            await SendAsync(hostSocket, roster, token).ConfigureAwait(false);
        }
    }

    private static Task SendAsync(WebSocket socket, StreamControl message, CancellationToken token) =>
        SocketSend.SendAsync(socket, message, token);

    private static void ApplyRoomMetadata(Room room, StreamControl message)
    {
        room.IsPrivate = message.IsPrivate ?? room.IsPrivate;

        if (message.Description is not null)
        {
            var trimmed = message.Description.Trim();
            room.Description = trimmed.Length == 0 ? null : trimmed[..Math.Min(trimmed.Length, 280)];
        }

        if (message.Location is not null)
        {
            var trimmed = message.Location.Trim();
            room.Location = trimmed.Length == 0 ? null : trimmed[..Math.Min(trimmed.Length, 120)];
        }

        if (message.Kind is { } kind)
        {
            room.Kind = kind;
        }

        if (message.Password is not null)
        {
            if (message.Password.Length == 0)
            {
                room.PasswordHash = null;
                if (room.Kind == RoomKind.Locked)
                {
                    room.Kind = RoomKind.Public;
                }
            }
            else
            {
                room.PasswordHash = HashPassword(message.Password);
            }
        }

        if (room.Kind == RoomKind.Locked && room.PasswordHash is null)
        {
            room.Kind = RoomKind.Public;
        }
    }

    private static StreamControl SanitizeState(StreamControl message) =>
        message with { Password = null };

    private static string HashPassword(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    private static bool PasswordMatches(string expectedHexHash, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var actual = Encoding.UTF8.GetBytes(HashPassword(password));
        var expected = Encoding.UTF8.GetBytes(expectedHexHash);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
