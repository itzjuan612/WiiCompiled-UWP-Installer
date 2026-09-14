# Builds the portable installer: a single self-contained WiiCompiled-UWP-Installer.exe
# (the whole .NET runtime is embedded) with the bundled certs/ and patches/ folders
# next to it. Ship the folder (or a zip of it); no .NET install is needed on the
# target machine.
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
# Never ship certificate private keys in the published folder (the release notes
# promise it: the tool mints its own dev cert locally).
Get-ChildItem $out -Recurse -Filter *.pfx | Remove-Item -Force

"Published: $out"
"Run $out\WiiCompiled-UWP-Installer.exe (GUI) or add --headless <steps> (CLI)."
"Keep certs\ and patches\ next to the exe."
