using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Talks to the Xbox Development Portal REST API (Dev Mode add-on) to install
/// a signed appx and (optionally) the dev certificate, then confirms the
/// package shows up installed. Same endpoint sequence the proven PowerShell
/// recipe uses: seed auth cookie -> CSRF token -> multipart upload -> poll
/// state -> verify package list.
/// </summary>
public sealed class XboxDeployService : IDisposable
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private string? _csrf;

    public XboxDeployService(string portalUrl, string user, string password)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            Credentials = new NetworkCredential(user, password),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // dev-mode self-signed cert
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(portalUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<bool> EnsureAuthAsync(CancellationToken ct)
    {
        var seed = await _http.GetAsync("api/os/info/", HttpCompletionOption.ResponseContentRead, ct);
        if (!seed.IsSuccessStatusCode) return false;
        foreach (Cookie c in _cookies.GetCookies(_http.BaseAddress!))
            if (string.Equals(c.Name, "CSRF-Token", StringComparison.OrdinalIgnoreCase))
                _csrf = c.Value;
        return _csrf != null;
    }

    private HttpRequestMessage WithCsrf(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (_csrf != null) request.Headers.Add("X-CSRF-Token", _csrf);
        return request;
    }

    /// <summary>Installs the developer certificate (.cer) so the signed appx is trusted. Safe to repeat.</summary>
    public async Task<bool> InstallCertificateAsync(string cerPath, IProgress<string> progress, CancellationToken ct)
    {
        if (!await EnsureAuthAsync(ct)) { progress.Report("Dev Portal auth failed (check IP/user/password)."); return false; }
        var body = new MultipartFormDataContent();
        var content = new ByteArrayContent(await File.ReadAllBytesAsync(cerPath, ct));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(content, "file", Path.GetFileName(cerPath));
        var certRequest = WithCsrf(HttpMethod.Post, "api/app/packagemanager/certificate");
        certRequest.Content = body;
        var response = await _http.SendAsync(certRequest, ct);
        progress.Report($"certificate upload: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeployAppxAsync(string appxPath, string expectVersion,
        IProgress<string> progress, CancellationToken ct)
    {
        if (!await EnsureAuthAsync(ct)) { progress.Report("Dev Portal auth failed (check IP/user/password)."); return false; }
        var leaf = Path.GetFileName(appxPath);

        progress.Report($"== Uploading {leaf} ==");
        var body = new MultipartFormDataContent();
        await using (var stream = File.OpenRead(appxPath))
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Add(content, "file", leaf);
            // The appx name MUST also be in the query string (portal quirk).
            var upload = WithCsrf(HttpMethod.Post, $"api/app/packagemanager/package?package={Uri.EscapeDataString(leaf)}");
            upload.Content = body;
            var response = await _http.SendAsync(upload, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            progress.Report($"upload: {(int)response.StatusCode} {text}");
            if (!response.IsSuccessStatusCode) return false;
        }

        progress.Report("Waiting for install...");
        var deadline = DateTime.UtcNow.AddMinutes(10);
        string? finalState = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(3000, ct);
            var state = await _http.GetAsync("api/app/packagemanager/state", ct);
            if (state.StatusCode == HttpStatusCode.NoContent) continue; // still deploying
            finalState = await state.Content.ReadAsStringAsync(ct);
            break;
        }
        if (finalState == null) { progress.Report("Deploy timed out."); return false; }
        progress.Report($"state: {finalState}");
        if (finalState.Contains("\"Success\":false", StringComparison.OrdinalIgnoreCase) ||
            finalState.Contains("Error", StringComparison.OrdinalIgnoreCase) &&
            !finalState.Contains("\"Success\":true", StringComparison.OrdinalIgnoreCase))
            return false;

        return await VerifyInstalledAsync(expectVersion, progress, ct);
    }

    public async Task<bool> VerifyInstalledAsync(string expectVersion, IProgress<string> progress, CancellationToken ct)
    {
        if (_csrf == null && !await EnsureAuthAsync(ct)) return false;
        var response = await _http.GetAsync("api/app/packagemanager/packages", ct);
        if (!response.IsSuccessStatusCode) { progress.Report("package list query failed."); return false; }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var hits = new List<string>();
        if (doc.RootElement.TryGetProperty("InstalledPackages", out var list))
            foreach (var pkg in list.EnumerateArray())
            {
                var full = pkg.TryGetProperty("PackageFullName", out var n) ? n.GetString() : null;
                if (full != null && full.Contains($"_{expectVersion}_neutral__")) hits.Add(full);
            }
        if (hits.Count == 0)
        {
            progress.Report($"Version {expectVersion} not found installed on console.");
            return false;
        }
        foreach (var h in hits) progress.Report($"Installed: {h}");
        return true;
    }

    public void Dispose() => _http.Dispose();
}
