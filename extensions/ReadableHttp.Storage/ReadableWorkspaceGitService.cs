using System.Diagnostics;

namespace ReadableHttp.Storage;

public sealed class ReadableWorkspaceGitService
{
    public Task<string> StatusAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        return RunGitAsync(workspacePath, "status --short --branch", cancellationToken);
    }

    public Task<string> PullAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        return RunGitAsync(workspacePath, "pull", cancellationToken);
    }

    public Task<string> PushAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        return RunGitAsync(workspacePath, "push", cancellationToken);
    }

    private static async Task<string> RunGitAsync(
        string workspacePath,
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git process.");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        }

        return string.IsNullOrWhiteSpace(output) ? error : output;
    }
}
