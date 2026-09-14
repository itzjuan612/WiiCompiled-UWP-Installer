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
    private string? _csrf;

    public XboxDeployService(string portalUrl, string user, string password)
    {
        var handler = new HttpClientHandler
        {
            // Manual cookie handling: HttpClientHandler's CookieContainer silently DROPS the
            // portal's "Set-Cookie: CSRF-Token=..." (its value contains '+' and the container
            // rejects it on an IP host), which left every deploy without a CSRF token.
            UseCookies = false,
            Credentials = new NetworkCredential(user, password),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // dev-mode self-signed cert
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(portalUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>
    /// Seeds authentication and captures the CSRF-Token cookie. Judged by the cookie, not
    /// the status code: the portal answers /api/os/info with 200, but the trailing-slash
    /// variant 404s while still issuing the cookie - treating that as failure made every
    /// deploy report "auth failed" with perfectly valid credentials.
    /// </summary>
    public async Task<bool> EnsureAuthAsync(IProgress<string>? progress, CancellationToken ct)
    {
        _csrf = null;
        try
        {
            using var seed = await _http.GetAsync("api/os/info", HttpCompletionOption.ResponseHeadersRead, ct);
            if (seed.StatusCode == HttpStatusCode.Unauthorized)
            {
                progress?.Report("Dev Portal rejected the credentials (401). Check user/password on the Deploy page.");
                return false;
            }
            if (seed.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var cookie in cookies)
                {
                    var m = Regex.Match(cookie, @"CSRF-Token=([^;]+)", RegexOptions.IgnoreCase);
                    if (m.Success) _csrf = m.Groups[1].Value;
                }
        }
        catch (HttpRequestException e)
        {
            progress?.Report($"Cannot reach the console at {_http.BaseAddress} ({e.Message}). " +
                             "Is it powered on, in Dev Mode, with the Developer Portal running?");
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            progress?.Report($"Timed out reaching the console at {_http.BaseAddress}.");
            return false;
        }
        if (_csrf == null)
            progress?.Report("Dev Portal did not issue a CSRF token (unexpected response). Is this a Dev Mode portal?");
        return _csrf != null;
    }

    /// <summary>Connection test: auths and lists the console's deployed packages.</summary>
    public async Task<bool> ReportPackagesAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!await EnsureAuthAsync(progress, ct)) return false;
        var response = await _http.GetAsync("api/app/packagemanager/packages", ct);
        if (!response.IsSuccessStatusCode)
        {
            progress.Report($"package list query failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            return false;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var total = 0;
        var ours = new List<string>();
        if (doc.RootElement.TryGetProperty("InstalledPackages", out var list))
            foreach (var pkg in list.EnumerateArray())
            {
                total++;
                var full = pkg.TryGetProperty("PackageFullName", out var n) ? n.GetString() : null;
                if (full != null && full.Contains("e2f1c9a4", StringComparison.OrdinalIgnoreCase)) ours.Add(full);
            }
        progress.Report($"Console reachable; {total} package(s) installed.");
        foreach (var p in ours) progress.Report($"  WiiCompiled package: {p}");
        if (ours.Count == 0) progress.Report("  (no WiiCompiled packages installed yet)");
        return true;
    }

    private HttpRequestMessage WithCsrf(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (_csrf != null)
        {
            request.Headers.Add("X-CSRF-Token", _csrf);
            request.Headers.TryAddWithoutValidation("Cookie", $"CSRF-Token={_csrf}");
        }
        return request;
    }

    /// <summary>Installs the developer certificate (.cer) so the signed appx is trusted. Safe to repeat.</summary>
    public async Task<bool> InstallCertificateAsync(string cerPath, IProgress<string> progress, CancellationToken ct)
    {
        if (!await EnsureAuthAsync(progress, ct)) return false;
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
        if (!await EnsureAuthAsync(progress, ct)) return false;
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
        if (_csrf == null && !await EnsureAuthAsync(progress, ct)) return false;
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
