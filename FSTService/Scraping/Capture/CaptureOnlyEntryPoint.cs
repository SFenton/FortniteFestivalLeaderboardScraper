using FSTService.Scraping.Replay;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;

namespace FSTService.Scraping.Capture;

public static class CaptureOnlyEntryPoint
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        using var interrupt =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);
        using var signals =
            CaptureProcessSignals.Register(
                interrupt);

        try
        {
            var command = CaptureOnlyCommand.Parse(args);
            CaptureEnvironmentFile.LoadCurrentDirectory();
            var environment =
                CaptureOnlyExecutionEnvironment
                    .FromProcessEnvironment();
            var options = LoadScraperOptions();
            return await RunAsync(
                command,
                environment,
                options,
                overrides: null,
                Console.Out,
                interrupt.Token);
        }
        catch (OperationCanceledException)
            when (interrupt
                .IsCancellationRequested)
        {
            WriteFailure(
                Console.Out,
                CaptureOnlyFailureKind
                    .Cancelled,
                CaptureOnlyExitCode.Cancelled,
                "Capture was cancelled.");
            return (int)CaptureOnlyExitCode.Cancelled;
        }
        catch (OperationCanceledException)
        {
            WriteFailure(
                Console.Out,
                CaptureOnlyFailureKind
                    .UnexpectedFailure,
                CaptureOnlyExitCode
                    .UnexpectedFailure,
                "Capture failed unexpectedly.");
            return (int)CaptureOnlyExitCode
                .UnexpectedFailure;
        }
        catch (CaptureOnlyException exception)
        {
            WriteFailure(
                Console.Out,
                exception.Kind,
                exception.ExitCode,
                exception.Message);
            return (int)exception.ExitCode;
        }
        catch (Exception)
        {
            WriteFailure(
                Console.Out,
                CaptureOnlyFailureKind
                    .UnexpectedFailure,
                CaptureOnlyExitCode
                    .UnexpectedFailure,
                "Capture failed unexpectedly.");
            return (int)CaptureOnlyExitCode
                .UnexpectedFailure;
        }
    }

    internal static async Task<int> RunAsync(
        CaptureOnlyCommand command,
        CaptureOnlyExecutionEnvironment environment,
        ScraperOptions options,
        CaptureOnlyCompositionOverrides? overrides,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            var services =
                CaptureOnlyComposition
                    .CreateServiceCollection(
                        environment,
                        options,
                        overrides);
            using var provider =
                services.BuildServiceProvider(
                    new ServiceProviderOptions
                    {
                        ValidateOnBuild = true,
                        ValidateScopes = true,
                    });
            var result = await provider
                .GetRequiredService<
                    ICaptureOnlyRunner>()
                .ExecuteAsync(
                    command,
                    cancellationToken);
            WriteSuccess(output, result);
            return (int)CaptureOnlyExitCode.Success;
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
            WriteFailure(
                output,
                CaptureOnlyFailureKind
                    .Cancelled,
                CaptureOnlyExitCode.Cancelled,
                "Capture was cancelled.");
            return (int)CaptureOnlyExitCode.Cancelled;
        }
        catch (OperationCanceledException)
        {
            WriteFailure(
                output,
                CaptureOnlyFailureKind
                    .UnexpectedFailure,
                CaptureOnlyExitCode
                    .UnexpectedFailure,
                "Capture failed unexpectedly.");
            return (int)CaptureOnlyExitCode
                .UnexpectedFailure;
        }
        catch (CaptureOnlyException exception)
        {
            WriteFailure(
                output,
                exception.Kind,
                exception.ExitCode,
                exception.Message);
            return (int)exception.ExitCode;
        }
        catch (Exception)
        {
            WriteFailure(
                output,
                CaptureOnlyFailureKind
                    .UnexpectedFailure,
                CaptureOnlyExitCode
                    .UnexpectedFailure,
                "Capture failed unexpectedly.");
            return (int)CaptureOnlyExitCode
                .UnexpectedFailure;
        }
    }

    internal static ScraperOptions LoadScraperOptions()
    {
        var currentDirectory =
            Directory.GetCurrentDirectory();
        var environmentName =
            Environment.GetEnvironmentVariable(
                "DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT");
        var configuration =
            new ConfigurationBuilder()
                .AddJsonFile(
                    Path.Combine(
                        currentDirectory,
                        "appsettings.json"),
                    optional: true,
                    reloadOnChange: false);
        if (!string.IsNullOrWhiteSpace(
                environmentName))
        {
            configuration.AddJsonFile(
                Path.Combine(
                    currentDirectory,
                    $"appsettings.{environmentName}.json"),
                optional: true,
                reloadOnChange: false);
        }
        var root = configuration
            .AddEnvironmentVariables()
            .Build();
        return root
                   .GetSection(
                       ScraperOptions.Section)
                   .Get<ScraperOptions>()
               ?? new ScraperOptions();
    }

    private static void WriteSuccess(
        TextWriter output,
        CaptureOnlyResult result) =>
        output.WriteLine(
            TierZeroCanonicalJson.SerializeToString(
                new
                {
                    status = "completed",
                    kind = "capture-only",
                    captureId = result.CaptureId,
                    packageRootHash =
                        result.PackageRootHash,
                    scopes = result.ScopeCount,
                    pages = result.PageCount,
                    entries = result.EntryCount,
                    requests = result.RequestCount,
                    responseBytes =
                        result.ResponseBytes,
                    noPublication = true,
                }));

    private static void WriteFailure(
        TextWriter output,
        CaptureOnlyFailureKind kind,
        CaptureOnlyExitCode exitCode,
        string message) =>
        output.WriteLine(
            TierZeroCanonicalJson.SerializeToString(
                new
                {
                    status = "failed",
                    kind = kind.ToString(),
                    exitCode = (int)exitCode,
                    message,
                    noPublication = true,
                }));
}

internal static class CaptureProcessSignals
{
    internal static IDisposable Register(
        CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        return new Registration(cancellation);
    }

    internal static void RequestTermination(
        CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        if (!cancellation.IsCancellationRequested)
            cancellation.Cancel();
    }

    private sealed class Registration : IDisposable
    {
        private readonly CancellationTokenSource
            _cancellation;
        private readonly ConsoleCancelEventHandler
            _consoleHandler;
        private readonly PosixSignalRegistration?
            _termination;
        private int _disposed;

        internal Registration(
            CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            _consoleHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                RequestTermination(_cancellation);
            };
            Console.CancelKeyPress += _consoleHandler;
            if (!OperatingSystem.IsWindows())
            {
                _termination =
                    PosixSignalRegistration.Create(
                        PosixSignal.SIGTERM,
                        context =>
                        {
                            context.Cancel = true;
                            RequestTermination(
                                _cancellation);
                        });
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }
            Console.CancelKeyPress -=
                _consoleHandler;
            _termination?.Dispose();
        }
    }
}
