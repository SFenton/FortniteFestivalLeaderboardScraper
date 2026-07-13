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
        bool runOnceRequested) =>
        mode == HostedWorkerMode.RegistrationSyncWorker ||
        (mode == HostedWorkerMode.FullWorker && !runOnceRequested);
}
