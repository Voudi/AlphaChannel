namespace AlphaChannel.Contracts;

public sealed record RadioCredentialsDto(
    string ListenUrl,
    string SourceHost,
    int SourcePort,
    string Mount,
    string SourceUser,
    string? SourcePassword);
