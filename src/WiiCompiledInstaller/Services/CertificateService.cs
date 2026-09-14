using System.Diagnostics;
using WiiCompiledInstaller.Models;

namespace WiiCompiledInstaller.Services;

/// <summary>
/// Creates a self-signed code-signing certificate for packing .appx files. The
/// repo never carries a private key (certs\*.pfx is gitignored), so every user
/// mints their own; the subject must equal the package manifest Publisher.
/// </summary>
public static class CertificateService
{
    public static bool Exists(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.PfxPath) && File.Exists(settings.PfxPath);

    /// <summary>
    /// Generates the key with PowerShell New-SelfSignedCertificate and exports a
    /// PFX + CER into the installer's certs folder. Returns false when the cert
    /// cannot be created (the caller should then leave the appx unsigned).
    /// </summary>
    public static async Task<bool> CreateAsync(AppSettings settings, string installerRoot, string subject, string password,
                                               Action<string> log, CancellationToken ct)
    {
        var certDir = Path.Combine(installerRoot, "certs");
        Directory.CreateDirectory(certDir);
        var pfx = Path.Combine(certDir, "mkwii-dev.pfx");
        var cer = Path.Combine(certDir, "mkwii-dev.cer");

        var script =
            "$ErrorActionPreference = 'Stop'\n" +
            "$pfx = '" + pfx.Replace("'", "''") + "'\n" +
            "$cer = '" + cer.Replace("'", "''") + "'\n" +
            "$pw  = ConvertTo-SecureString '" + password.Replace("'", "''") + "' -AsPlainText -Force\n" +
            "$thumb = (New-SelfSignedCertificate -Subject '" + subject.Replace("'", "''") + "' " +
            "-CertStoreLocation Cert:\\CurrentUser\\My -KeyExportPolicy Exportable " +
            "-KeyLength 2048 -Provider 'Microsoft Enhanced RSA and AES Cryptographic Provider' " +
            "-TextExtension 2.5.29.37={text}1.3.6.1.5.5.7.3.3 -NotAfter (Get-Date).AddYears(5)).Thumbprint\n" +
            "$sec = ConvertTo-SecureString '" + password.Replace("'", "''") + "' -AsPlainText -Force\n" +
            "Export-PfxCertificate -Cert Cert:\\CurrentUser\\My\\$thumb -FilePath $pfx -Password $sec | Out-Null\n" +
            "Export-Certificate -Cert Cert:\\CurrentUser\\My\\$thumb -FilePath $cer | Out-Null\n" +
            "Write-Output $thumb\n";

        var tmp = Path.Combine(Path.GetTempPath(), "mkw-cert-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(tmp, script);
        try
        {
            log("Creating self-signed certificate: " + subject);
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + tmp + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit(120000);
            ct.ThrowIfCancellationRequested();
            var thumb = (await stdoutTask).Trim();
            if (!p.HasExited || p.ExitCode != 0)
            {
                var err = (await stderrTask).Trim();
                log("Certificate creation failed" + (string.IsNullOrEmpty(err) ? "." : ": " + err));
                return false;
            }
            settings.PfxPath = pfx;
            settings.PfxPassword = password;
            log("Certificate ready (thumbprint " + thumb + ").");
            log("Deploying to an Xbox also needs this .cer installed on the console; the Deploy tab uploads it automatically.");
            return true;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
