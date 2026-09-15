using System.Diagnostics;

namespace WinOldRecovery.Core.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        ProcessStartInfo startInfo = new(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Close stdin so children cannot wait on a parent console prompt
            // (e.g. `net user /add` Y/N for passwords longer than 14 characters).
            RedirectStandardInput = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach ((string key, string? value) in request.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Windows did not start child process '{request.FileName}'.");
        }

        process.StandardInput.Close();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        TimeSpan timeout = request.Timeout ?? DefaultTimeout;
        int timeoutMs = timeout.TotalMilliseconds <= 0
            ? 1
            : (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);

        bool exited;
        try
        {
            exited = await Task.Run(
                    () => process.WaitForExit(timeoutMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        if (!exited)
        {
            TryKill(process);
            throw new TimeoutException(
                $"Child process '{request.FileName}' exceeded its {timeout} timeout.");
        }

        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The child may already be gone or not killable from this integrity level.
        }
    }
}
