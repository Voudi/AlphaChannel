using System.Diagnostics;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Manages the local BgUtils HTTP provider.
///
/// The provider can be started when YouTube compatibility mode is
/// activated and stopped after a successful Android probe.
///
/// Only a provider process started by this instance will be stopped.
/// An already-running external provider is reused but never killed.
/// </summary>
internal sealed class YouTubePoTokenService : IDisposable
{
    private const int Port =
        4416;

    private static readonly Uri PingUri =
        new(
            $"http://127.0.0.1:{Port}/ping");

    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource shutdown;
    private readonly SemaphoreSlim lifecycleLock =
        new(
            1,
            1);

    private Process? ownedProcess;
    private bool usingExternalProvider;
    private bool stopping;
    private bool disposed;

    internal YouTubePoTokenService()
    {
        httpClient =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(1)
            };

        shutdown =
            new CancellationTokenSource();
    }

    internal bool IsRunning =>
        usingExternalProvider ||
        ownedProcess is
        {
            HasExited: false
        };

    /// <summary>
    /// Compatibility wrapper for the current Resources implementation.
    /// It can be removed after Resources stops launching the service
    /// automatically.
    /// </summary>
    internal async Task StartAsync()
    {
        await EnsureStartedAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures that the local provider is ready and returns true when
    /// its /ping endpoint responds successfully.
    /// </summary>
    internal async Task<bool> EnsureStartedAsync()
    {
        if (disposed)
        {
            return false;
        }

        await lifecycleLock
            .WaitAsync(
                shutdown.Token)
            .ConfigureAwait(false);

        try
        {
            if (disposed)
            {
                return false;
            }

            //
            // This also detects a provider started by another instance.
            //
            if (await IsProviderHealthyAsync(
                    shutdown.Token)
                .ConfigureAwait(false))
            {
                if (ownedProcess is null)
                {
                    usingExternalProvider =
                        true;

                    AepLog.Info(
                        "[YouTube/PO] Reusing an existing local HTTP provider.");
                }

                return true;
            }

            usingExternalProvider =
                false;

            //
            // A previously owned process may have exited.
            //
            if (ownedProcess is not null)
            {
                StopOwnedProcess();
            }

            if (!TryGetRuntimePaths(
                    out var nodePath,
                    out var serverDirectory,
                    out var mainScript))
            {
                AepLog.Warning(
                    "[YouTube/PO] HTTP provider files are unavailable. " +
                    "The script provider remains available.");

                return false;
            }

            var process =
                new Process
                {
                    StartInfo =
                        new ProcessStartInfo
                        {
                            FileName =
                                nodePath,

                            WorkingDirectory =
                                serverDirectory,

                            UseShellExecute =
                                false,

                            CreateNoWindow =
                                true,

                            RedirectStandardOutput =
                                true,

                            RedirectStandardError =
                                true
                        },

                    EnableRaisingEvents =
                        true
                };

            process.StartInfo.ArgumentList.Add(
                mainScript);

            //
            // The provider's patched main.ts binds to 127.0.0.1.
            // Version 1.3.2 only supports selecting the port here.
            //
            process.StartInfo.ArgumentList.Add(
                "--port");

            process.StartInfo.ArgumentList.Add(
                Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));

            process.OutputDataReceived +=
                OnProviderOutput;

            process.ErrorDataReceived +=
                OnProviderError;

            process.Exited +=
                OnProviderExited;

            if (!process.Start())
            {
                process.Dispose();

                AepLog.Warning(
                    "[YouTube/PO] The local HTTP provider could not be started.");

                return false;
            }

            ownedProcess =
                process;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            AepLog.Info(
                "[YouTube/PO] Starting local HTTP provider on 127.0.0.1:4416.");

            for (var attempt = 0;
                 attempt < 25;
                 attempt++)
            {
                shutdown.Token
                    .ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    AepLog.Warning(
                        "[YouTube/PO] Local HTTP provider exited during startup.");

                    StopOwnedProcess();
                    return false;
                }

                if (await IsProviderHealthyAsync(
                        shutdown.Token)
                    .ConfigureAwait(false))
                {
                    AepLog.Info(
                        "[YouTube/PO] Local HTTP provider is ready.");

                    return true;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(200),
                        shutdown.Token)
                    .ConfigureAwait(false);
            }

            AepLog.Warning(
                "[YouTube/PO] Local HTTP provider did not become ready.");

            StopOwnedProcess();
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                "[YouTube/PO] Local HTTP provider startup failed: " +
                exception.Message);

            StopOwnedProcess();
            return false;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops the provider if Alpha Channel started it.
    /// An externally-owned provider is left running.
    /// </summary>
    internal async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        await lifecycleLock
            .WaitAsync()
            .ConfigureAwait(false);

        try
        {
            if (ownedProcess is null)
            {
                //
                // We do not own an already-running external provider.
                //
                usingExternalProvider =
                    false;

                return;
            }

            stopping =
                true;

            StopOwnedProcess();

            AepLog.Info(
                "[YouTube/PO] Local HTTP provider stopped.");
        }
        finally
        {
            stopping =
                false;

            lifecycleLock.Release();
        }
    }

    private async Task<bool> IsProviderHealthyAsync(
        CancellationToken token)
    {
        try
        {
            using var response =
                await httpClient.GetAsync(
                        PingUri,
                        token)
                    .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body =
                await response.Content
                    .ReadAsStringAsync(
                        token)
                    .ConfigureAwait(false);

            return
                body.Contains(
                    "server_uptime",
                    StringComparison.OrdinalIgnoreCase) &&
                body.Contains(
                    "version",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetRuntimePaths(
        out string nodePath,
        out string serverDirectory,
        out string mainScript)
    {
        var rootDirectory =
            Path.Combine(
                Plugin.PluginInterface
                    .ConfigDirectory
                    .FullName,
                "youtube-po-token");

        nodePath =
            Path.Combine(
                rootDirectory,
                "node",
                "node.exe");

        serverDirectory =
            Path.Combine(
                rootDirectory,
                "provider",
                "server");

        mainScript =
            Path.Combine(
                serverDirectory,
                "build",
                "main.js");

        return
            File.Exists(nodePath) &&
            Directory.Exists(serverDirectory) &&
            File.Exists(mainScript);
    }

    private static void OnProviderOutput(
      object sender,
      DataReceivedEventArgs args)
    {
        var message =
            args.Data;

        if (string.IsNullOrWhiteSpace(
                message) ||
            IsRoutineOrSensitiveProviderMessage(
                message))
        {
            return;
        }

        AepLog.Debug(
            $"[YouTube/PO Server] {message}");
    }

    private static void OnProviderError(
        object sender,
        DataReceivedEventArgs args)
    {
        var message =
            args.Data;

        if (string.IsNullOrWhiteSpace(
                message) ||
            IsRoutineOrSensitiveProviderMessage(
                message))
        {
            return;
        }

        //
        // Unexpected stderr is worth retaining, but never forward
        // generated token values into the Dalamud log.
        //
        AepLog.Warning(
            $"[YouTube/PO Server] {message}");
    }

    private static bool IsRoutineOrSensitiveProviderMessage(
        string message)
    {
        return
            message.StartsWith(
                "Started POT server",
                StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith(
                "Using challenge",
                StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith(
                "Generating POT",
                StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith(
                "Generated IntegrityToken:",
                StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith(
                "poToken:",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "\"integrityToken\"",
                StringComparison.OrdinalIgnoreCase);
    }

    private void OnProviderExited(
        object? sender,
        EventArgs args)
    {
        if (!disposed &&
            !stopping)
        {
            AepLog.Warning(
                "[YouTube/PO] Local HTTP provider stopped unexpectedly. " +
                "The script provider remains available.");
        }
    }

    private void StopOwnedProcess()
    {
        var process =
            Interlocked.Exchange(
                ref ownedProcess,
                null);

        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -=
                OnProviderExited;

            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);

                process.WaitForExit(
                    2000);
            }
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                "[YouTube/PO] Could not stop the local provider cleanly: " +
                exception.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed =
            true;

        shutdown.Cancel();

        var lockTaken =
            false;

        try
        {
            lockTaken =
                lifecycleLock.Wait(TimeSpan.FromSeconds(3));

            if (!lockTaken)
            {
                AepLog.Warning(
                    "[YouTube/PO] Provider lifecycle worker did not stop within 3 seconds.");
            }

            stopping =
                true;

            StopOwnedProcess();
        }
        finally
        {
            if (lockTaken)
            {
                lifecycleLock.Release();
                lifecycleLock.Dispose();
            }
        }

        shutdown.Dispose();
        httpClient.Dispose();
    }
}
