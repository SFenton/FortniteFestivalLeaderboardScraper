using FSTService.Persistence;
using FSTService.Scraping;
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
        var metaDatabase =
            context.RequestServices
                .GetRequiredService<IMetaDatabase>();
        var registrationMutations =
            context.RequestServices
                .GetRequiredService<
                    RegistrationMutationCoordinator>();
        var maxScoreMaintenance =
            context.RequestServices
                .GetService<PublicReadGateService>()
                ?.GetState()
                .MaxScoreMaintenance == true;
        await RecordActivityIfNeededAsync(
            context,
            metaDatabase,
            registrationMutations,
            _rolloutReadOnly,
            maxScoreMaintenance,
            context.RequestAborted);
    }

    internal static async Task RecordActivityIfNeededAsync(
        HttpContext context,
        IMetaDatabase metaDatabase,
        RegistrationMutationCoordinator?
            registrationMutations,
        bool rolloutReadOnly,
        bool maxScoreMaintenance = false,
        CancellationToken ct = default)
    {
        if (rolloutReadOnly
            || maxScoreMaintenance
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

        if (registrationMutations is null)
            return;

        try
        {
            await using var registrationLease =
                await registrationMutations
                    .AcquireWriteLeaseAsync(ct);
            switch (selection)
            {
                case SelectedPlayerSelection player:
                    metaDatabase.TouchWebRegistrationActivity(
                        player.AccountId);
                    break;
                case SelectedBandSelection band:
                    metaDatabase.RegisterSelectedBandActivity(
                        band.BandType,
                        band.TeamKey,
                        band.BandId);
                    break;
            }
        }
        catch (RegistrationMutationBlockedException)
        {
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
        }
    }
}
