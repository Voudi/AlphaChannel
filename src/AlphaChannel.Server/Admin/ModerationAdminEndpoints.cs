using AlphaChannel.Contracts;
using AlphaChannel.Server.Moderation;

namespace AlphaChannel.Server.Admin;

internal static class ModerationAdminEndpoints
{
    public static void MapModerationAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin").AddEndpointFilter<AdminTokenFilter>();

        group.MapGet("/reports", async (ModerationAdminService moderation, CancellationToken ct) =>
            Results.Ok(await moderation.GetOpenReportsAsync(ct)));

        group.MapPost("/reports/{id:guid}/resolve", async (Guid id, ResolveReportRequest request, ModerationAdminService moderation, CancellationToken ct) =>
            await moderation.ResolveAsync(id, request.Action, request.Note, request.SuspendUntilUnix, ct) ? Results.Ok() : Results.NotFound());

        group.MapPost("/accounts/{accountId:guid}/ban", async (Guid accountId, BanAccountRequest request, ModerationAdminService moderation, CancellationToken ct) =>
        {
            await moderation.BanAsync(accountId, request.Reason, request.UntilUnix, ct);
            return Results.Ok();
        });

        group.MapPost("/accounts/{accountId:guid}/unban", async (Guid accountId, ModerationAdminService moderation, CancellationToken ct) =>
        {
            await moderation.UnbanAsync(accountId, ct);
            return Results.Ok();
        });

        group.MapPatch("/accounts/{accountId:guid}", async (Guid accountId, AdminPatchAccountRequest request, ModerationAdminService moderation, CancellationToken ct) =>
            await moderation.PatchAccountAsync(accountId, request.PatreonTier, request.IsDeveloper, ct)
                ? Results.Ok()
                : Results.NotFound());
    }
}
