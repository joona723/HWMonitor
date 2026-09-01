using System.Diagnostics;
using System.Net.Http;

namespace HWMonitor;

static class AutoUpdater
{
    /// <summary>
    /// Downloads the update's exe asset and hands off to a detached PowerShell script that waits for
    /// this process to exit, overwrites the running exe in place, and relaunches it. Returns true once
    /// the handoff script has been started — the caller is responsible for exiting the app afterward.
    /// </summary>
    public static async Task<bool> DownloadAndRelaunchAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            return false;
        }

        string updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HWMonitor", "update");
        Directory.CreateDirectory(updateDir);
        string newExePath = Path.Combine(updateDir, "HWMonitor.new.exe");
        string scriptPath = Path.Combine(updateDir, "apply-update.ps1");

        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"HWMonitor/{UpdateChecker.CurrentVersion}");
            using HttpResponseMessage response = await client
                .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using FileStream fileStream = File.Create(newExePath);
            await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(newExePath) || new FileInfo(newExePath).Length == 0)
        {
            return false;
        }

        string targetExePath = Application.ExecutablePath;
        int currentPid = Environment.ProcessId;

        string script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            try { Wait-Process -Id {{currentPid}} -Timeout 30 } catch {}
            for ($i = 0; $i -lt 20; $i++) {
                try {
                    Copy-Item -Path '{{newExePath}}' -Destination '{{targetExePath}}' -Force -ErrorAction Stop
                    break
                } catch {
                    Start-Sleep -Milliseconds 500
                }
            }
            Remove-Item -Path '{{newExePath}}' -Force
            Start-Process -FilePath '{{targetExePath}}'
            Remove-Item -Path '{{scriptPath}}' -Force
            """;
        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
        return true;
    }
}
