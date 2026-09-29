using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HeroTweaker.Core.Services;

public record UpdateCheckResult(bool HasUpdate, string LatestTag, string ReleaseUrl, string ReleaseNotes);

public static class UpdateCheckerService
{
    private const string RepoOwner = "krestyan1n";
    private const string RepoName = "HeroTweaker";

    // Текущая версия приложения
    public const string CurrentVersion = "0.8.7.4";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2.5)
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
                return new UpdateCheckResult(false, string.Empty, string.Empty, string.Empty);

            var jsonStream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
            string htmlUrl = root.GetProperty("html_url").GetString() ?? string.Empty;
            string body = root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;

            bool isNewer = CompareVersions(tagName, CurrentVersion);

            return new UpdateCheckResult(isNewer, tagName, htmlUrl, body);
        }
        catch
        {
            return new UpdateCheckResult(false, string.Empty, string.Empty, string.Empty);
        }
    }

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