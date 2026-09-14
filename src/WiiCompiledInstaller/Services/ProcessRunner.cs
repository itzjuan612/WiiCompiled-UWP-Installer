using System.Diagnostics;
using System.Text;

namespace WiiCompiledInstaller.Services;

/// <summary>Result of a completed external command.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs external tools (git, cmake, ninja, makeappx, signtool, curl) with live line-by-line
/// output so the GUI can stream it, while still capturing the full stdout for parsing.
/// A single instance serialises jobs so long builds never overlap.
/// </summary>
public sealed class ProcessRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Launches <paramref name="fileName"/> with <paramref name="arguments"/>, reporting each
    /// stdout/stderr line to <paramref name="onLine"/> on the caller's synchronization context.
    /// </summary>
    public async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        IProgress<string>? onLine = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (environment is not null)
                foreach (var (k, v) in environment)
                {
                    // Sentinel key from ToolchainLocator.BuildEnvironment: prepend onto the
                    // inherited PATH so cl/link/rc stay reachable alongside the system dirs.
                    if (string.Equals(k, "PATH_PREPEND", StringComparison.OrdinalIgnoreCase))
                    {
                        var current = psi.Environment["PATH"] ?? "";
                        psi.Environment["PATH"] = string.IsNullOrEmpty(v) ? current : v + ";" + current;
                        continue;
                    }
                    psi.Environment[k] = v;
                }

            var stdout = new StringBuilder();
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            void Emit(string? line)
            {
                if (line is null) return;
                stdout.AppendLine(line);
                onLine?.Report(line);
            }

            process.OutputDataReceived += (_, e) => Emit(e.Data);
            process.ErrorDataReceived += (_, e) => Emit(e.Data);

            onLine?.Report($"> {Path.GetFileName(fileName)} {arguments}");

            if (!process.Start())
                return new CommandResult(-1, "", false);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } }))
            {
                var effectiveTimeout = timeout ?? Timeout.InfiniteTimeSpan;
                bool finished = await WaitExitAsync(process, effectiveTimeout, ct).ConfigureAwait(false);
                if (!finished)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new CommandResult(-1, stdout.ToString(), true);
                }
            }

            // Ensure async readers have flushed.
            process.WaitForExit();
            return new CommandResult(process.ExitCode, stdout.ToString(), false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> WaitExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return !ct.IsCancellationRequested; // timeout (not user cancel) => still "not finished cleanly"
        }
    }
}
