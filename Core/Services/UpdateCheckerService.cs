using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HeroTweaker.Core.Services;

public record UpdateCheckResult(
    bool HasUpdate,
    bool CanInstall,
    string LatestTag,
    string ReleaseUrl,
    string ReleaseNotes,
    string PackageUrl,
    string PackageName,
    string PackageSha256,
    long PackageSize,
    string Status);

public static class UpdateCheckerService
{
    private const string RepoOwner = "krestyan1n";
    private const string RepoName = "HeroTweaker";
    public const string PackageAssetName = "HeroTweaker-win-x64.zip";

    public static string CurrentVersion
    {
        get
        {
            var version = typeof(UpdateCheckerService).Assembly.GetName().Version;
            return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static UpdateCheckerService()
    {
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "HeroTweaker-UpdateChecker");
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await HttpClient.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
                return EmptyResult($"GitHub вернул HTTP {(int)response.StatusCode}.");

            var jsonStream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
            string htmlUrl = root.GetProperty("html_url").GetString() ?? string.Empty;
            string body = root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
            bool isNewer = CompareVersions(tagName, CurrentVersion);
            if (!isNewer)
                return new UpdateCheckResult(false, false, tagName, htmlUrl, body, string.Empty, string.Empty, string.Empty, 0, $"Установлена версия {CurrentVersion}; последний релиз — {tagName}.");

            var package = root.TryGetProperty("assets", out var assets)
                ? assets.EnumerateArray().FirstOrDefault(asset => string.Equals(GetString(asset, "name"), PackageAssetName, StringComparison.OrdinalIgnoreCase))
                : default;
            if (package.ValueKind != JsonValueKind.Object)
                return new UpdateCheckResult(true, false, tagName, htmlUrl, body, string.Empty, string.Empty, string.Empty, 0,
                    $"Доступен {tagName}, но в релизе ещё нет {PackageAssetName}.");

            var packageUrl = GetString(package, "browser_download_url");
            var packageName = GetString(package, "name");
            var digest = GetString(package, "digest");
            var size = package.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var packageSize) ? packageSize : 0;
            var digestValue = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty;
            var canInstall = IsTrustedReleaseAssetUrl(packageUrl) && packageName.Equals(PackageAssetName, StringComparison.OrdinalIgnoreCase) &&
                size is > 0 and <= 800L * 1024 * 1024 && Regex.IsMatch(digestValue, "^[a-fA-F0-9]{64}$");
            var status = canInstall ? $"{tagName} готов к загрузке ({FormatSize(size)})." : "Метаданные ZIP не прошли проверку безопасности; установка недоступна.";
            return new UpdateCheckResult(true, canInstall, tagName, htmlUrl, body, packageUrl, packageName, digestValue, size, status);
        }
        catch (Exception ex)
        {
            return EmptyResult($"Не удалось проверить GitHub Releases: {ex.Message}");
        }
    }

    private static UpdateCheckResult EmptyResult(string status) =>
        new(false, false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, status);

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static bool IsTrustedReleaseAssetUrl(string rawUrl) =>
        Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{RepoOwner}/{RepoName}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024d / 1024d:F1} ГБ"
        : $"{bytes / 1024d / 1024d:F0} МБ";

    private static bool CompareVersions(string remoteTag, string currentVer)
    {
        try
        {
            string cleanRemote = Regex.Match(remoteTag, @"\d+(\.\d+)+").Value;
            string cleanCurrent = Regex.Match(currentVer, @"\d+(\.\d+)+").Value;

            if (Version.TryParse(cleanRemote, out var vRemote) &&
                Version.TryParse(cleanCurrent, out var vCurrent))
            {
                return vRemote > vCurrent;
            }
        }
        catch { }

        return false;
    }

    public static void OpenReleaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
