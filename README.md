# WiiCompiled-UWP-Installer

A portable Windows GUI that automates building the **WiiCompiled** UWP package
(.appx) for Xbox Series consoles, with Retro Rewind support and one-click
deployment through the Xbox Dev Portal.

Source-only tool: it never bundles game code. You must supply your own legally
dumped PAL (RMCP01) copy of Mario Kart Wii (`main.dol` + `StaticR.rel`) and your
own Retro Rewind package. The game translation runs locally on your machine.

## What it does

1. **Setup** — clones `itzjuan612/WiiCompiled-Xbox-UWP` into `workspace\`, runs the
   repo's own `Prepare-PortableTools.ps1` / `Prepare-Dependencies.ps1` bootstraps,
   locates MSVC + the Windows SDK (no more hardcoded drive letters), and verifies
   your disc dump hashes.
2. **Retro Rewind** — imports a `RetroRewind6` pack (folder or zip), stages it where
   the translator's `retro-rewind` profile expects it, and uses the translator's own
   `check-base-mod-awareness` gate so a new Retro Rewind release only retranslates
   what actually changed.
3. **Build** — translates the base game + the mod, then configures and compiles
   `WiiCompiled.exe` + `RetroRewind.exe` for UWP (MSVC).
4. **Config** — edits the `[video]` defaults that ship inside the package
   (`Config.default.toml`), manages the package version.
5. **Deploy** — packages (makeappx + signtool with your .pfx) and installs to the
   Xbox over the Dev Portal (CSRF-aware REST), or to this PC via Add-AppxPackage.

## Requirements

- Windows 10/11 x64
- .NET 8 SDK (to run from source) — the published app is self-contained
- Visual Studio 2022 with the C++ desktop workload (MSVC)
- Windows SDK with UWP components (UnionMetadata/Windows.winmd)
- git on PATH

## Status

Working. The whole pipeline is validated end to end on a fresh clone:

1. **Setup** - clone the repo, bootstrap the portable tools (CMake/Ninja/llvm-mingw
   via the repo's own prepare scripts), locate VS2022/MSVC + the Windows SDK on any
   drive, verify the dumped `main.dol` / `StaticR.rel`.
2. **Retro Rewind** - stage a `RetroRewind6` folder (or just the `Binaries\Code.pul`
   inside an update .zip) into the workspace and inspect its version. A new RR
   release only needs a rebuild - the source is not pinned to an RR version.
3. **Build** - translate the base + RR mod shards, configure the UWP/MSVC CMake
   build, compile `WiiCompiled.exe` + `RetroRewind.exe` (the first build is long,
   afterwards it is incremental).
4. **Config** - edit the `[video]` defaults that seed a device's first-run
   `Config.toml` (resolution multiplier, frame interpolation, shader-compile
   workers, prewarm memory floor, overlay hotkey) and bump the package version.
5. **Deploy** - pack + sign the .appx with the bundled dev certificate and upload
   it to the Xbox Dev Portal (automatic certificate install, CSRF handshake,
   install polling), or install it on a PC with `Add-AppxPackage`.

Everything the GUI does is also scriptable:

```text
WiiCompiled-Installer --headless preflight bootstrap translate configure build package deploy
```

`publish.ps1` produces the portable, self-contained folder-drop under
`publish\WiiCompiled-Installer\`.

## Legal

GPL-3.0. Not affiliated with Nintendo or the Retro Rewind team.
