using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Viegard.PipelineHost.Workers;

internal interface IHostUpgradeAgentLauncher
{
    bool IsWindows { get; }

    string AppBaseDirectory { get; }

    ValueTask<string?> GetCloneHeadAsync(string cloneRoot, CancellationToken cancellationToken);

    ValueTask<HostUpgradeAgentProcessResult> RunScheduledTaskAsync(
        string scheduledTaskName,
        CancellationToken cancellationToken);

    ValueTask<HostUpgradeAgentProcessResult> LaunchDetachedUpgradeAsync(
        string satelliteScriptPath,
        string clientName,
        string transcriptPath,
        CancellationToken cancellationToken);
}

internal sealed record HostUpgradeAgentProcessResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

internal sealed class ProcessHostUpgradeAgentLauncher : IHostUpgradeAgentLauncher
{
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string AppBaseDirectory => AppContext.BaseDirectory;

    public async ValueTask<string?> GetCloneHeadAsync(string cloneRoot, CancellationToken cancellationToken)
    {
        // The clone is typically owned by the interactive administrator while
        // this process runs as the service account (and the upgrade task as
        // SYSTEM).  Git refuses repositories owned by another user unless the
        // path is whitelisted; pass safe.directory inline so the agent's read
        // does not depend on host-level git configuration (the installer also
        // whitelists the clone system-wide for the scheduled task's script).
        var result = await RunProcessAsync(
            "git",
            ["-c", $"safe.directory={cloneRoot}", "-C", cloneRoot, "rev-parse", "HEAD"],
            workingDirectory: cloneRoot,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
    }

    public ValueTask<HostUpgradeAgentProcessResult> RunScheduledTaskAsync(
        string scheduledTaskName,
        CancellationToken cancellationToken) =>
        RunProcessAsync(
            "schtasks.exe",
            ["/Run", "/TN", scheduledTaskName],
            workingDirectory: null,
            cancellationToken);

    public async ValueTask<HostUpgradeAgentProcessResult> LaunchDetachedUpgradeAsync(
        string satelliteScriptPath,
        string clientName,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        var transcriptDirectory = Path.GetDirectoryName(transcriptPath);
        if (!string.IsNullOrWhiteSpace(transcriptDirectory))
        {
            Directory.CreateDirectory(transcriptDirectory);
        }

        var command = BuildElevatedLaunchCommand(satelliteScriptPath, clientName, transcriptPath);
        return await RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
            workingDirectory: Path.GetDirectoryName(Path.GetFullPath(satelliteScriptPath)),
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildElevatedLaunchCommand(
        string satelliteScriptPath,
        string clientName,
        string transcriptPath)
    {
        var innerCommand = string.Join(
            "; ",
            [
                "$ErrorActionPreference = 'Stop'",
                "$transcript = " + PowerShellSingleQuote(transcriptPath),
                "try { & "
                    + PowerShellSingleQuote(satelliteScriptPath)
                    + " upgrade -Client "
                    + PowerShellSingleQuote(clientName)
                    + " -Yes *>&1 | Tee-Object -FilePath $transcript; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }; exit 0 }"
                    + " catch { $_ | Out-String | Add-Content -LiteralPath $transcript; exit 1 }",
            ]);

        var innerArguments = new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            innerCommand,
        };

        return "Start-Process -FilePath 'powershell.exe' -ArgumentList @("
            + string.Join(", ", innerArguments.Select(PowerShellSingleQuote))
            + ") -Verb RunAs -WindowStyle Hidden";
    }

    private static string PowerShellSingleQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async ValueTask<HostUpgradeAgentProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return new HostUpgradeAgentProcessResult(-1, $"Could not start {fileName}.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = new StringBuilder();
            output.Append(await standardOutput.ConfigureAwait(false));
            output.Append(await standardError.ConfigureAwait(false));
            return new HostUpgradeAgentProcessResult(process.ExitCode, output.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HostUpgradeAgentProcessResult(-1, ex.Message);
        }
    }
}
