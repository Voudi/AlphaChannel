using System.Net.WebSockets;
using AlphaChannel.Contracts;
using AlphaChannel.Server.Auth;
using AlphaChannel.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AlphaChannel.Server.Moderation;

// Admin-only (see Admin/AdminTokenFilter). Suspend/ban here is the account-level enforcement point:
// it revokes every active session immediately (not just future sign-ins) and force-closes any live
// /rt socket, rather than waiting for the ban to naturally take effect next reconnect.
internal sealed class ModerationAdminService(IDbContextFactory<AlphaChannelDbContext> dbFactory, AccountService accounts, UserDirectory directory)
{
    public async Task<List<AdminReportDto>> GetOpenReportsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var reports = await db.Reports.Where(r => r.Status == ReportStatus.Open).OrderBy(r => r.CreatedAtUtc).ToListAsync(cancellationToken);

        var accountIds = reports.SelectMany(r => new[] { r.ReporterAccountId, r.ReportedAccountId }).Distinct().ToList();
        var accountsById = (await db.Accounts.Where(a => accountIds.Contains(a.Id)).ToListAsync(cancellationToken)).ToDictionary(a => a.Id);

        return reports.Select(r => new AdminReportDto(
            r.Id.ToString(),
            r.ReporterAccountId.ToString(), accountsById.GetValueOrDefault(r.ReporterAccountId)?.Handle ?? "(unknown)",
            r.ReportedAccountId.ToString(), accountsById.GetValueOrDefault(r.ReportedAccountId)?.Handle ?? "(unknown)",
            r.Reason, r.Details, r.RevealedBody, r.FrankingVerified, r.Status.ToString(), ToUnixSeconds(r.CreatedAtUtc)))
            .ToList();
    }

    public async Task<bool> ResolveAsync(
        Guid reportId, AdminReportAction action, string? note, long? suspendUntilUnix, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
        {
            return false;
        }

        report.Status = action == AdminReportAction.Dismiss ? ReportStatus.Dismissed : ReportStatus.ActionTaken;
        report.ReviewedAtUtc = DateTime.UtcNow;
        report.ReviewNote = note;
        await db.SaveChangesAsync(cancellationToken);

        switch (action)
        {
            case AdminReportAction.Suspend:
                var until = suspendUntilUnix is { } u ? DateTimeOffset.FromUnixTimeSeconds(u).UtcDateTime : DateTime.UtcNow.AddDays(7);
                await ApplyBanAsync(report.ReportedAccountId, note ?? "Suspended following a report.", until, cancellationToken);
                break;
            case AdminReportAction.Ban:
                await ApplyBanAsync(report.ReportedAccountId, note ?? "Banned following a report.", null, cancellationToken);
                break;
        }

        return true;
    }

    public Task BanAsync(Guid accountId, string reason, long? untilUnix, CancellationToken cancellationToken) =>
        ApplyBanAsync(accountId, reason, untilUnix is { } u ? DateTimeOffset.FromUnixTimeSeconds(u).UtcDateTime : null, cancellationToken);

    public async Task UnbanAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return;
        }

        account.IsBanned = false;
        account.BanReason = null;
        account.BannedAtUtc = null;
        account.BannedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> PatchAccountAsync(
        Guid accountId, PatreonTier? patreonTier, bool? isDeveloper, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return false;
        }

        if (patreonTier is { } tier)
        {
            account.PatreonTier = tier;
        }

        if (isDeveloper is { } developer)
        {
            account.IsDeveloper = developer;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ApplyBanAsync(Guid accountId, string reason, DateTime? until, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return;
        }

        account.IsBanned = true;
        account.BanReason = reason;
        account.BannedAtUtc = DateTime.UtcNow;
        account.BannedUntilUtc = until;
        await db.SaveChangesAsync(cancellationToken);

        await accounts.RevokeAllTokensAsync(accountId, cancellationToken);

        if (directory.TryGetSocket(accountId.ToString(), out var socket) && socket is { State: WebSocketState.Open })
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Account suspended.", cancellationToken);
        }
    }

    private static long ToUnixSeconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
