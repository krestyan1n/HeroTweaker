using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace HeroTweaker.Core.Services;

public static class SelfUpdateService
{
    private const long MaxArchiveExpandedBytes = 1024L * 1024 * 1024;
    private const int MaxArchiveEntries = 5000;

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(15) };

    private const string ApplyScript = """
        param(
            [int]$TargetPid,
            [string]$InstallDirectory,
            [string]$StageDirectory,
            [string]$JobDirectory,
            [string]$ManifestPath,
            [string]$BackupDirectory,
            [string]$DataDirectory,
            [string]$ExecutableName
        )
        $ErrorActionPreference = 'Stop'
        $logPath = Join-Path $DataDirectory 'Updates\last-update.log'
        function Write-UpdateLog([string]$Message) {
            $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
            Add-Content -LiteralPath $logPath -Value "[$stamp] $Message" -Encoding UTF8
        }
        function Assert-SafeRelativePath([string]$Path) {
            if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.Contains(':')) { throw "Unsafe update path: $Path" }
            $parts = $Path.Replace('\','/').Split('/')
            if ($parts | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' }) { throw "Unsafe update path: $Path" }
        }
        try {
            New-Item -ItemType Directory -Force -Path $DataDirectory, $BackupDirectory | Out-Null
            Write-UpdateLog "Waiting for process $TargetPid to exit."
            for ($i = 0; $i -lt 180; $i++) {
                if (-not (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Seconds 1
            }
            if (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue) { throw 'HeroTweaker did not close in time.' }

            $InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
            $StageDirectory = [IO.Path]::GetFullPath($StageDirectory)
            $newFiles = @((Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).files)
            if ($newFiles.Count -eq 0 -or -not ($newFiles -contains $ExecutableName)) { throw 'Update package manifest is incomplete.' }
            foreach ($file in $newFiles) { Assert-SafeRelativePath ([string]$file) }

            $installedManifest = Join-Path $DataDirectory 'installed-files.json'
            $oldFiles = @($ExecutableName)
            if (Test-Path -LiteralPath $installedManifest) {
                $oldFiles = @((Get-Content -LiteralPath $installedManifest -Raw -Encoding UTF8 | ConvertFrom-Json).files)
                foreach ($file in $oldFiles) { Assert-SafeRelativePath ([string]$file) }
                Copy-Item -LiteralPath $installedManifest -Destination (Join-Path $BackupDirectory 'installed-files.json') -Force
            }

            foreach ($file in $oldFiles) {
                $source = Join-Path $InstallDirectory ([string]$file)
                if (Test-Path -LiteralPath $source -PathType Leaf) {
                    $backup = Join-Path $BackupDirectory ([string]$file)
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
                    Copy-Item -LiteralPath $source -Destination $backup -Force
                }
            }

            try {
                foreach ($file in $newFiles) {
                    $source = Join-Path $StageDirectory ([string]$file)
                    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing staged file: $file" }
                    $destination = Join-Path $InstallDirectory ([string]$file)
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                    Copy-Item -LiteralPath $source -Destination $destination -Force
                }

                $newSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($file in $newFiles) { [void]$newSet.Add([string]$file) }
                foreach ($file in $oldFiles) {
                    if (-not $newSet.Contains([string]$file)) {
                        $obsolete = Join-Path $InstallDirectory ([string]$file)
                        if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
                    }
                }

                $manifestTemp = "$installedManifest.tmp"
                [IO.File]::WriteAllText($manifestTemp, (ConvertTo-Json -InputObject @{ files = @($newFiles) } -Depth 4), [Text.UTF8Encoding]::new($false))
                Move-Item -LiteralPath $manifestTemp -Destination $installedManifest -Force
                $appPath = Join-Path $InstallDirectory $ExecutableName
                if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw 'Updated executable is missing.' }
                Start-Process -FilePath $appPath -WorkingDirectory $InstallDirectory | Out-Null
                Write-UpdateLog 'Update succeeded and the application restarted.'
                Remove-Item -LiteralPath $BackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $JobDirectory -Recurse -Force -ErrorAction SilentlyContinue
                exit 0
            }
            catch {
                $updateError = $_.Exception.Message
                Write-UpdateLog "Update failed; restoring backup. $updateError"
                foreach ($file in $newFiles) {
                    $destination = Join-Path $InstallDirectory ([string]$file)
                    if (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue }
                }
                foreach ($file in $oldFiles) {
                    $backup = Join-Path $BackupDirectory ([string]$file)
                    if (Test-Path -LiteralPath $backup -PathType Leaf) {
                        $destination = Join-Path $InstallDirectory ([string]$file)
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                        Copy-Item -LiteralPath $backup -Destination $destination -Force
                    }
                }
                $oldManifestBackup = Join-Path $BackupDirectory 'installed-files.json'
                if (Test-Path -LiteralPath $oldManifestBackup) { Copy-Item -LiteralPath $oldManifestBackup -Destination $installedManifest -Force }
                else { Remove-Item -LiteralPath $installedManifest -Force -ErrorAction SilentlyContinue }
                Write-UpdateLog 'Rollback completed.'
                $noticePath = Join-Path $DataDirectory 'Updates\pending-error.txt'
                try { Set-Content -LiteralPath $noticePath -Value "Не удалось завершить обновление. Предыдущая версия восстановлена.`r`n`r`n$updateError" -Encoding UTF8 } catch { }
                Start-Process -FilePath (Join-Path $InstallDirectory $ExecutableName) -WorkingDirectory $InstallDirectory | Out-Null
                exit 1
            }
        }
        catch {
            Write-UpdateLog "Updater could not begin: $($_.Exception.Message)"
            $noticePath = Join-Path $DataDirectory 'Updates\pending-error.txt'
            try { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $noticePath) | Out-Null; Set-Content -LiteralPath $noticePath -Value "Не удалось запустить обновление.`r`n`r`n$($_.Exception.Message)" -Encoding UTF8 } catch { }
            exit 2
        }
        """;

    public static async Task DownloadVerifyAndLaunchAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.HasUpdate || !update.CanInstall)
            throw new InvalidOperationException("Для найденного релиза нет проверенного ZIP-пакета.");
        if (!Uri.TryCreate(update.PackageUrl, UriKind.Absolute, out var assetUri) || assetUri.Scheme != Uri.UriSchemeHttps ||
            !assetUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !assetUri.AbsolutePath.StartsWith("/krestyan1n/HeroTweaker/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Адрес пакета не прошёл проверку.");

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("Не удалось определить путь запущенного HeroTweaker.exe.");
        var executableName = Path.GetFileName(executablePath);
        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var filesystemRoot = Path.GetPathRoot(installDirectory);
        if (string.Equals(installDirectory.TrimEnd(Path.DirectorySeparatorChar), filesystemRoot?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Обновление из корня диска запрещено; поместите приложение в отдельную папку.");
        var windowsDirectory = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (installDirectory.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Обновление из системной папки Windows заблокировано.");

        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker");
        var updateRoot = Path.Combine(dataDirectory, "Updates");
        var jobDirectory = Path.Combine(updateRoot, $"{SanitizeTag(update.LatestTag)}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(jobDirectory, update.PackageName);
        var stageDirectory = Path.Combine(jobDirectory, "staged");
        var manifestPath = Path.Combine(jobDirectory, "package-manifest.json");
        var backupDirectory = Path.Combine(updateRoot, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);

        try
        {
            using var response = await HttpClient.GetAsync(update.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var actualContentLength = response.Content.Headers.ContentLength;
            if (actualContentLength.HasValue && actualContentLength.Value != update.PackageSize)
                throw new InvalidDataException("Размер ZIP не совпадает с метаданными релиза.");

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > update.PackageSize || received > 800L * 1024 * 1024)
                        throw new InvalidDataException("Загружаемый архив превышает размер, заявленный GitHub.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(Math.Min(65, received * 65d / update.PackageSize));
                }
                if (received != update.PackageSize)
                    throw new InvalidDataException("Загрузка архива завершилась не полностью.");
            }

            await using (var archiveStream = File.OpenRead(archivePath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken));
                if (!actualHash.Equals(update.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 архива не совпадает с digest GitHub Releases.");
            }
            progress?.Report(70);

            var files = ValidateArchive(archivePath, executableName);
            Directory.CreateDirectory(stageDirectory);
            ZipFile.ExtractToDirectory(archivePath, stageDirectory, overwriteFiles: false);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new PackageManifest(files), new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            progress?.Report(90);

            Directory.CreateDirectory(Path.GetDirectoryName(GetApplyScriptPath())!);
            await File.WriteAllTextAsync(GetApplyScriptPath(), ApplyScript, cancellationToken);
            LaunchUpdater(Environment.ProcessId, installDirectory, stageDirectory, jobDirectory, manifestPath, backupDirectory, dataDirectory, executableName);
            progress?.Report(100);
        }
        catch
        {
            try { if (Directory.Exists(jobDirectory)) Directory.Delete(jobDirectory, recursive: true); } catch { }
            try { if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true); } catch { }
            throw;
        }
    }

    private static string[] ValidateArchive(string archivePath, string executableName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException("В ZIP некорректное количество файлов.");

        long expandedBytes = 0;
        var files = new List<string>();
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containsExecutable = false;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
                throw new InvalidDataException("Архив содержит абсолютный путь.");
            var segments = normalized.Split('/');
            if (segments.Any(segment => segment is ".." or "."))
                throw new InvalidDataException("Архив содержит путь за пределы папки приложения.");
            if (string.IsNullOrEmpty(segments[^1])) continue;
            if (!uniquePaths.Add(normalized))
                throw new InvalidDataException("Архив содержит повторяющиеся пути.");

            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
                throw new InvalidDataException("Архив с символическими ссылками не разрешён.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxArchiveExpandedBytes)
                throw new InvalidDataException("Распакованный архив превышает лимит 1 ГБ.");

            files.Add(normalized.Replace('/', Path.DirectorySeparatorChar));
            if (normalized.Equals(executableName, StringComparison.OrdinalIgnoreCase)) containsExecutable = true;
        }

        if (!containsExecutable)
            throw new InvalidDataException($"В корне архива не найден {executableName}.");
        return files.ToArray();
    }

    private static void LaunchUpdater(int processId, string installDirectory, string stageDirectory, string jobDirectory, string manifestPath, string backupDirectory, string dataDirectory, string executableName)
    {
        var powerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShellPath)) powerShellPath = "powershell.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(GetApplyScriptPath());
        AddArgument(startInfo, "-TargetPid", processId.ToString());
        AddArgument(startInfo, "-InstallDirectory", installDirectory);
        AddArgument(startInfo, "-StageDirectory", stageDirectory);
        AddArgument(startInfo, "-JobDirectory", jobDirectory);
        AddArgument(startInfo, "-ManifestPath", manifestPath);
        AddArgument(startInfo, "-BackupDirectory", backupDirectory);
        AddArgument(startInfo, "-DataDirectory", dataDirectory);
        AddArgument(startInfo, "-ExecutableName", executableName);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows не запустила updater-процесс.");
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string GetApplyScriptPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker", "Updates", "apply-update.ps1");

    public static string? TakePendingUpdateError()
    {
        var noticePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker", "Updates", "pending-error.txt");
        try
        {
            if (!File.Exists(noticePath)) return null;
            var message = File.ReadAllText(noticePath);
            File.Delete(noticePath);
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch { return null; }
    }

    private static string SanitizeTag(string tag) => string.Concat(tag.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));

    private sealed record PackageManifest(string[] Files);
}
