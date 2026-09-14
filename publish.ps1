# Builds the portable, folder-drop installer: WiiCompiled-UWP-Installer.exe plus
# the bundled certs/ and patches/ trees next to it. Publish once, ship the
# folder (or a zip of it); no .NET install is needed on the target machine.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\WiiCompiledInstaller\WiiCompiledInstaller.csproj'
$out  = Join-Path $root 'publish\WiiCompiled-UWP-Installer'

dotnet publish $proj -c Release -r win-x64 --self-contained true -o $out

foreach ($dir in 'certs', 'patches') {
    $src = Join-Path $root $dir
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $out $dir) -Recurse -Force
    }
}

"Published: $out"
"Run $out\WiiCompiled-UWP-Installer.exe (GUI) or add --headless <steps> (CLI)."
