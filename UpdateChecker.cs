using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace HWMonitor;

sealed record UpdateInfo(Version Version, string TagName, string Url, string? DownloadUrl);

static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/joona723/HWMonitor/releases/latest";
    private const string UpdateAssetName = "HWMonitor.exe";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HWMonitor", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using HttpResponseMessage response = await client.GetAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // No releases published yet, rate-limited, or offline — treat as "no update".
            return null;
        }

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        string? tagName = root.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() : null;
        string? url = root.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!TryParseVersion(tagName, out Version? latest) || latest is null)
        {
            return null;
        }

        if (latest <= CurrentVersion)
        {
            return null;
        }

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? assetName = asset.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
                if (string.Equals(assetName, UpdateAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlProp) ? urlProp.GetString() : null;
                    break;
                }
            }
        }

        return new UpdateInfo(latest, tagName, url, downloadUrl);
    }

    private static bool TryParseVersion(string tagName, out Version? version)
    {
        string trimmed = tagName.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out version);
    }
}
