// <copyright file="AutoUpdate.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace Launcher
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;

    public sealed record ReleaseInfo(string Version, string DownloadUrl);

    public static class AutoUpdate
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/JoongDa/GameHelper2/releases/latest";
        private static readonly HttpClient HttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
        private static string? extractedPath;
        private static string? stagingDirectory;
        private static string? newVersion;

        static AutoUpdate()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "GameHelper-Launcher");
        }

        public static async Task<ReleaseInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await HttpClient.GetStringAsync(ReleasesApiUrl, timeout.Token).ConfigureAwait(false);
            var release = JObject.Parse(response);
            var version = release["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(version) || !version.StartsWith('v') ||
                !Version.TryParse(version[1..], out var parsedVersion) || parsedVersion.Build < 0 || parsedVersion.Revision >= 0)
            {
                throw new InvalidDataException("The release has an invalid version tag.");
            }

            // The repository publishes one asset; its filename is not part of the contract.
            var assets = release["assets"] as JArray;
            var downloadUrl = assets is { Count: > 0 } ? assets[0]["browser_download_url"]?.ToString() : null;
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("The release has no valid download URL.");
            }

            return new ReleaseInfo(version, downloadUrl!);
        }

        public static bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            return Version.TryParse(latestVersion.TrimStart('v'), out var latest) &&
                Version.TryParse(currentVersion.TrimStart('v'), out var current) && latest > current;
        }

        public static async Task PrepareUpdateAsync(
            ReleaseInfo release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            extractedPath = null;
            stagingDirectory = null;
            newVersion = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var tempDir = Path.Combine(Path.GetTempPath(), $"GameHelperUpdate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, "update.zip");
            await DownloadFileWithProgressAsync(release.DownloadUrl, zipPath, progress, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();

            var extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            timeout.Token.ThrowIfCancellationRequested();
            var executables = Directory.GetFiles(extractDir, "GameHelper.exe", SearchOption.AllDirectories);
            if (executables.Length != 1)
            {
                throw new InvalidDataException("The update must contain exactly one GameHelper.exe.");
            }

            var payloadDir = Path.GetDirectoryName(executables[0])!;
            if (!File.Exists(Path.Combine(payloadDir, "GameHelper.dll")) ||
                !File.Exists(Path.Combine(payloadDir, "Launcher.exe")) ||
                !File.Exists(Path.Combine(payloadDir, "Launcher.dll")) ||
                !string.Equals(GetCurrentVersion(executables[0]), release.Version, StringComparison.Ordinal) ||
                !string.Equals(GetCurrentVersion(Path.Combine(payloadDir, "GameHelper.dll")), release.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The update is incomplete or its executable version does not match the release tag.");
            }

            extractedPath = payloadDir;
            stagingDirectory = tempDir;
            newVersion = release.Version;
        }

        private static string GetCurrentVersion(string gameHelperExePath)
        {
            var version = FileVersionInfo.GetVersionInfo(gameHelperExePath).FileVersion;
            if (!Version.TryParse(version, out var parsed))
            {
                throw new InvalidDataException("Cannot read the GameHelper executable version.");
            }

            return $"v{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        }

        private static async Task DownloadFileWithProgressAsync(
            string url, string destinationPath, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            using var response = await HttpClient.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            long totalDownloaded = 0;
            var lastPercentage = -1;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalDownloaded += bytesRead;
                if (totalBytes is > 0)
                {
                    var percentage = (int)Math.Min(100, totalDownloaded * 100 / totalBytes.Value);
                    if (percentage != lastPercentage)
                    {
                        lastPercentage = percentage;
                        progress?.Report(percentage);
                    }
                }
            }

            if (totalBytes.HasValue && totalDownloaded != totalBytes.Value)
            {
                throw new InvalidDataException("The update download is incomplete.");
            }

            progress?.Report(100);
        }

        // Start the helper before requesting a normal application shutdown.
        public static void LaunchUpdateAndExit(int processId)
        {
            if (string.IsNullOrEmpty(extractedPath) || string.IsNullOrEmpty(stagingDirectory) || string.IsNullOrEmpty(newVersion))
            {
                throw new InvalidOperationException("Update paths not initialized.");
            }

            var currentDir = Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory);
            var launcherPath = Path.Combine(currentDir, "Launcher.exe");
            var tempDir = Path.GetFullPath(stagingDirectory);
            var staging = new DirectoryInfo(tempDir);
            if (!staging.Name.StartsWith("GameHelperUpdate-", StringComparison.Ordinal) ||
                !string.Equals(staging.Parent?.FullName, Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Invalid update staging directory.");
            }

            var logPath = Path.Combine(tempDir, "update.log");
            var scriptPath = Path.Combine(tempDir, "install.ps1");
            var script = $@"
$ErrorActionPreference = 'Stop';
try {{
    $parent = Get-Process -Id {processId} -ErrorAction SilentlyContinue;
    if ($parent -and -not $parent.WaitForExit(30000)) {{ throw 'GameHelper did not exit within 30 seconds.'; }}
    foreach ($entry in Get-ChildItem -LiteralPath '{Quote(extractedPath)}' -Recurse -File -Force) {{
        $relative = $entry.FullName.Substring('{Quote(extractedPath)}'.Length).TrimStart([char[]]'\/');
        $target = Join-Path '{Quote(currentDir)}' $relative;
        if ($relative -match '(^|[\\/])configs?[\\/]' -and (Test-Path -LiteralPath $target)) {{ continue; }}
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null;
        Copy-Item -LiteralPath $entry.FullName -Destination $target -Force -ErrorAction Stop;
    }}
    foreach ($file in @('GameHelper.exe', 'GameHelper.dll')) {{
        $installed = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path '{Quote(currentDir)}' $file));
        $version = [Version]::Parse($installed.FileVersion);
        if ('v' + $version.ToString(3) -ne '{Quote(newVersion)}') {{ throw 'Installed application version does not match the release.'; }}
    }}
    Start-Process -FilePath '{Quote(launcherPath)}' -WorkingDirectory '{Quote(currentDir)}' -ErrorAction Stop;
    Remove-Item -LiteralPath '{Quote(tempDir)}' -Recurse -Force -ErrorAction SilentlyContinue;
}} catch {{
    $_ | Out-File -LiteralPath '{Quote(logPath)}' -Encoding utf8;
}}
";
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            using var helper = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start the update helper.");
        }

        private static string Quote(string value) => value.Replace("'", "''");
    }
}
