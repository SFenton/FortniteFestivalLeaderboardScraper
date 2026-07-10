using System.Runtime.CompilerServices;

namespace FSTService.Tests;

internal static class TestEnvironmentInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("EPIC_CLIENT_ID", "test-epic-client-id");
        Environment.SetEnvironmentVariable("EPIC_CLIENT_SECRET", "test-epic-client-secret");
    }
}
