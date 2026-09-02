using AlphaChannel.Contracts;
using AlphaChannel.Server.Auth;
using AlphaChannel.Server.Social;

namespace AlphaChannel.Server.Live;

internal static class RadioEndpoints
{
    public static void MapRadioEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/radio").AddEndpointFilter<AccountAuthFilter>().AddEndpointFilter<LalafellGateFilter>();

        group.MapGet("/me", (HttpContext context, RadioService radio) =>
            Results.Ok(radio.GetMine(context.GetAccount().Id, includePassword: false)));

        group.MapPost("/me", (HttpContext context, RadioService radio) =>
            Results.Ok(radio.GetMine(context.GetAccount().Id, includePassword: true)));
    }
}
