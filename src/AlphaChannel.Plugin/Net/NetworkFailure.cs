using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AlphaChannel.Plugin.Net;

internal enum NetworkFailure
{
    None,
    Cancelled,
    TimedOut,
    NoConnection,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    InvalidRequest,
    RateLimited,
    ServerUnavailable,
    InvalidResponse,
    Unknown,
}

internal sealed record NetworkFailureInfo(
    NetworkFailure Failure,
    string Operation,
    int? HttpStatus = null,
    string? Detail = null)
{
    internal string UserMessage => Failure switch
    {
        NetworkFailure.Cancelled => string.Empty,
        NetworkFailure.TimedOut => "The request took too long. Please try again.",
        NetworkFailure.NoConnection => "Could not connect. Check your internet connection.",
        NetworkFailure.Unauthorized => "Your session has expired. Please sign in again.",
        NetworkFailure.Forbidden => "You do not have permission to do that.",
        NetworkFailure.NotFound => "The requested item could not be found.",
        NetworkFailure.Conflict => "That action conflicts with an existing item.",
        NetworkFailure.InvalidRequest => "The server could not accept that request.",
        NetworkFailure.RateLimited => "Too many requests were sent. Please wait and try again.",
        NetworkFailure.ServerUnavailable => "Alpha Channel is temporarily unavailable.",
        NetworkFailure.InvalidResponse => "Alpha Channel returned an invalid response.",
        _ => "The network request failed. Please try again.",
    };
}

internal static class NetworkFailureClassifier
{
    private static readonly ConcurrentDictionary<string, NetworkFailureInfo> Recent =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsSuccess(
        string area,
        string operation,
        HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            Clear(area);
            return true;
        }

        FromResponse(area, operation, response);
        return false;
    }

    internal static NetworkFailureInfo FromResponse(
        string area,
        string operation,
        HttpResponseMessage response)
    {
        var failure = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => NetworkFailure.Unauthorized,
            HttpStatusCode.Forbidden => NetworkFailure.Forbidden,
            HttpStatusCode.NotFound => NetworkFailure.NotFound,
            HttpStatusCode.Conflict => NetworkFailure.Conflict,
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                NetworkFailure.InvalidRequest,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                NetworkFailure.TimedOut,
            (HttpStatusCode)429 => NetworkFailure.RateLimited,
            >= HttpStatusCode.InternalServerError => NetworkFailure.ServerUnavailable,
            _ => NetworkFailure.Unknown,
        };

        return Record(
            area,
            new NetworkFailureInfo(
                failure,
                operation,
                (int)response.StatusCode,
                response.ReasonPhrase));
    }

    internal static NetworkFailureInfo FromException(
        string area,
        string operation,
        Exception exception,
        bool cancellationRequested = false)
    {
        var failure = exception switch
        {
            OperationCanceledException when cancellationRequested => NetworkFailure.Cancelled,
            OperationCanceledException => NetworkFailure.TimedOut,
            HttpRequestException { InnerException: SocketException } => NetworkFailure.NoConnection,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => NetworkFailure.Unauthorized,
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => NetworkFailure.Forbidden,
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => NetworkFailure.NotFound,
            HttpRequestException { StatusCode: (HttpStatusCode)429 } => NetworkFailure.RateLimited,
            HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } =>
                NetworkFailure.ServerUnavailable,
            HttpRequestException => NetworkFailure.NoConnection,
            System.Text.Json.JsonException or Newtonsoft.Json.JsonException =>
                NetworkFailure.InvalidResponse,
            _ => NetworkFailure.Unknown,
        };

        return Record(
            area,
            new NetworkFailureInfo(
                failure,
                operation,
                exception is HttpRequestException requestException &&
                requestException.StatusCode is { } status
                    ? (int)status
                    : null,
                exception.Message));
    }

    internal static void Clear(string area) => Recent.TryRemove(area, out _);

    internal static NetworkFailureInfo? GetLast(string area) =>
        Recent.TryGetValue(area, out var failure) ? failure : null;

    private static NetworkFailureInfo Record(string area, NetworkFailureInfo info)
    {
        Recent[area] = info;

        if (info.Failure != NetworkFailure.Cancelled)
        {
            var status = info.HttpStatus is { } code ? $" HTTP {code}." : string.Empty;
            AepLog.Warning(
                $"[Network:{area}] {info.Operation} failed as {info.Failure}.{status} {info.Detail}".TrimEnd());
        }

        return info;
    }
}
