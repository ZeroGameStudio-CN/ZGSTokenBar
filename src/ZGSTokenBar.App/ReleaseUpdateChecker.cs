using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZGSTokenBar.App;

internal sealed record ReleaseUpdateInfo(
    Version Version,
    string TagName,
    Uri PageUri,
    Uri PackageUri,
    Uri ChecksumsUri);

internal sealed class ReleaseUpdateChecker : IDisposable
{
    internal const string LatestReleaseUrl =
        "https://api.github.com/repos/ZeroGameStudio-CN/ZGSTokenBar/releases/latest";
    internal const int MaximumResponseBytes = 256 * 1024;
    private readonly HttpClient _httpClient;

    public ReleaseUpdateChecker()
        : this(new HttpClient(new HttpClientHandler { UseCookies = false }))
    {
    }

    internal ReleaseUpdateChecker(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<ReleaseUpdateInfo?> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ZGSTokenBar", currentVersion.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var body = await ReadBoundedAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(body);
        return Parse(document.RootElement, currentVersion);
    }

    internal static ReleaseUpdateInfo? Parse(JsonElement root, Version currentVersion)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagElement)
            || !root.TryGetProperty("html_url", out var pageElement)
            || !root.TryGetProperty("assets", out var assetsElement)
            || assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Latest release response is incomplete.");
        }

        var tagName = tagElement.GetString();
        var page = pageElement.GetString();
        if (!TryParseTag(tagName, out var version) || version <= currentVersion) return null;
        if (!TryReleaseUri(page, "/ZeroGameStudio-CN/ZGSTokenBar/releases/", out var pageUri))
        {
            throw new JsonException("Latest release page is not a trusted GitHub URL.");
        }

        var versionText = version.ToString(3);
        var packageName = $"ZGSTokenBar-Portable-v{versionText}.zip";
        var checksumsName = $"ZGSTokenBar-v{versionText}-SHA256.txt";
        Uri? packageUri = null;
        Uri? checksumsUri = null;
        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement)
                || !asset.TryGetProperty("browser_download_url", out var urlElement))
            {
                continue;
            }
            var name = nameElement.GetString();
            var url = urlElement.GetString();
            if (!TryReleaseUri(
                    url,
                    $"/ZeroGameStudio-CN/ZGSTokenBar/releases/download/{tagName}/",
                    out var assetUri))
            {
                continue;
            }
            if (string.Equals(name, packageName, StringComparison.Ordinal)) packageUri = assetUri;
            if (string.Equals(name, checksumsName, StringComparison.Ordinal)) checksumsUri = assetUri;
        }

        return packageUri is not null && checksumsUri is not null
            ? new ReleaseUpdateInfo(version, tagName!, pageUri, packageUri, checksumsUri)
            : null;
    }

    internal static bool TryParseTag(string? tagName, out Version version)
    {
        version = new Version();
        if (tagName is not { Length: > 1 }
            || tagName[0] is not ('v' or 'V')
            || !Version.TryParse(tagName[1..], out var parsed)
            || parsed is null
            || parsed.Major < 0
            || parsed.Minor < 0
            || parsed.Build < 0
            || parsed.Revision >= 0)
        {
            return false;
        }
        version = parsed;
        return true;
    }

    private static bool TryReleaseUri(string? value, string pathPrefix, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && parsed.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal)
            && (uri = parsed) is not null;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("Latest release response is too large.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("Latest release response is too large.");
            }
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    public void Dispose() => _httpClient.Dispose();
}
