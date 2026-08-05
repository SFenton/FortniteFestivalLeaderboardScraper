using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public sealed class SelectedProfileActivityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _rolloutReadOnly;

    public SelectedProfileActivityMiddleware(
        RequestDelegate next,
        IOptions<ScraperOptions> options)
    {
        _next = next;
        _rolloutReadOnly = options.Value.RolloutReadOnlyStartup;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);
        if (_rolloutReadOnly
            || context.WebSockets.IsWebSocketRequest
            || !context.Request.Path.StartsWithSegments("/api")
            || context.Response.StatusCode >=
                StatusCodes.Status500InternalServerError)
        {
            return;
        }

        if (!SelectedProfileHeaders.TryParse(
                context.Request.Headers,
                out var selection)
            || selection is null)
        {
            return;
        }

        var metaDatabase =
            context.RequestServices.GetRequiredService<IMetaDatabase>();
        switch (selection)
        {
            case SelectedPlayerSelection player:
                metaDatabase.TouchWebRegistrationActivity(player.AccountId);
                break;
            case SelectedBandSelection band:
                metaDatabase.RegisterSelectedBandActivity(
                    band.BandType,
                    band.TeamKey,
                    band.BandId);
                break;
        }
    }
}
