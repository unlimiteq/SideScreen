using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace SideScreen;

public static class AppInfo
{
    public const string Repo = "unlimiteq/SideScreen";
    public static string RepoUrl => $"https://github.com/{Repo}";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}

/// <summary>Asks GitHub for the latest release. Only runs when the user clicks "Check for updates".</summary>
public static class Updater
{
    public sealed record Result(bool IsNewer, string? Tag, string? Url, string? Error);

    public static async Task<Result> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SideScreen", AppInfo.Version));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
            if (!resp.IsSuccessStatusCode) return new(false, null, null, $"GitHub answered {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? AppInfo.RepoUrl + "/releases";
            bool newer = System.Version.TryParse(tag.TrimStart('v', 'V'), out var latest)
                         && System.Version.TryParse(AppInfo.Version, out var mine) && latest > mine;
            return new(newer, tag, url, null);
        }
        catch (Exception e)
        {
            return new(false, null, null, e.Message);
        }
    }
}
