namespace FSTService;

internal enum HostedWorkerMode
{
    FullWorker,
    ApiOnly,
    FrontendOnly,
    RegistrationSyncWorker
}

internal static class HostedWorkerModeResolver
{
    public static bool RequiresNoHostedServices(
        bool soloFamilyRankingBackfillRequested,
        bool leaderboardRivalsRecomputeRequested,
        bool maxScoreMaintenanceRequested)
        => soloFamilyRankingBackfillRequested
           || leaderboardRivalsRecomputeRequested
           || maxScoreMaintenanceRequested;

    public static HostedWorkerMode Resolve(
        bool apiOnlyRequested,
        bool scraperWorkerDisabled,
        bool registrationSyncWorkerRequested)
    {
        if (apiOnlyRequested)
            return HostedWorkerMode.ApiOnly;

        if (registrationSyncWorkerRequested)
            return HostedWorkerMode.RegistrationSyncWorker;

        if (scraperWorkerDisabled)
            return HostedWorkerMode.FrontendOnly;

        return HostedWorkerMode.FullWorker;
    }

    public static bool ShouldRunRegistrationBackfillWorker(
        HostedWorkerMode mode,
        bool runOnceRequested,
        bool backfillOnlyRequested = false) =>
        mode == HostedWorkerMode.RegistrationSyncWorker ||
        (mode == HostedWorkerMode.FullWorker
         && !runOnceRequested
         && !backfillOnlyRequested);

    public static HostedServiceRegistrationPlan ResolveHostedServicePlan(
        HostedWorkerMode mode,
        bool rolloutReadOnlyStartup,
        bool runOnceRequested,
        bool backfillOnlyRequested)
    {
        if (rolloutReadOnlyStartup)
            return HostedServiceRegistrationPlan.ReadOnlyRollout;

        return new HostedServiceRegistrationPlan(
            RegisterStalenessMonitor: true,
            RegisterPublicationChangeMonitor: mode != HostedWorkerMode.FullWorker,
            RegisterSongCatalogRefresh: mode != HostedWorkerMode.FullWorker,
            RegisterRegistrationBackfill: ShouldRunRegistrationBackfillWorker(
                mode,
                runOnceRequested,
                backfillOnlyRequested),
            RegisterFullWorkerServices: mode == HostedWorkerMode.FullWorker);
    }
}

internal sealed record HostedServiceRegistrationPlan(
    bool RegisterStalenessMonitor,
    bool RegisterPublicationChangeMonitor,
    bool RegisterSongCatalogRefresh,
    bool RegisterRegistrationBackfill,
    bool RegisterFullWorkerServices)
{
    public static HostedServiceRegistrationPlan ReadOnlyRollout { get; } = new(
        RegisterStalenessMonitor: false,
        RegisterPublicationChangeMonitor: false,
        RegisterSongCatalogRefresh: false,
        RegisterRegistrationBackfill: false,
        RegisterFullWorkerServices: false);
}
