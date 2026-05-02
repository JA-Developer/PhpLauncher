# PhpLauncher

A **.NET MAUI** app that acts as a hybrid *launcher* for **Laravel** projects: it starts **[FrankenPHP](https://frankenphp.dev)** (Caddy + embedded PHP) in the background and opens the app in a **WebView** pointing at `https://localhost:<port>`.

**Not intended for mobile** (Android / iOS). The focus is **desktop** on **Windows**, **Linux**, and **macOS**: absolute paths to the Laravel project, the **FrankenPHP** binary on the same machine, and the WebView pointing at `https://localhost:<port>`. The `.csproj` may still list MAUI template TFMs that are not the product target; what matters is the desktop flow on those three systems.

Use it to package a **desktop app** experience on top of your existing Laravel backend without replacing Laravel’s own CLI: PhpLauncher is the native shell that orchestrates the server and the web window.

## Prerequisites

| Component | Notes |
|-----------|--------|
| [.NET 9 SDK](https://dotnet.microsoft.com/download) | Required to build. |
| [.NET MAUI workload](https://learn.microsoft.com/dotnet/maui/get-started/installation) | `dotnet workload install maui`. On **Windows**, Visual Studio or MAUI desktop workloads are common; on **macOS**, follow the docs (e.g. Xcode for Apple targets); on **Linux**, it depends on the MAUI support you have installed to build or publish. |
| [FrankenPHP](https://frankenphp.dev) | Official builds per OS and CPU. Point `FrankenPhpPath` at the folder that contains the executable (`frankenphp.exe` on Windows, `frankenphp` on Linux and macOS). |
| **Laravel** project | Typical layout with a `public/` folder (the generated `Caddyfile` uses `root * public/`). Ensure `storage/logs/` exists for the Caddy access log path used by the embedded template. |

**Target desktop platforms:** **Windows**, **macOS**, and **Linux**. In this repo, the usual MAUI desktop TFMs are **Windows** (`net9.0-windows10.0.19041.0`) and **Mac Catalyst** (`net9.0-maccatalyst`) for macOS. **Linux:** desktop MAUI on Linux is still evolving; the flow (Laravel, FrankenPHP, `ServerOptions`, `localhost`) is the same with POSIX paths and FrankenPHP’s Linux binary, but you may need to adjust the `.csproj` or your toolchain to build the launcher on your distribution.

## Clone and build

```bash
git clone https://github.com/JA-Developer/PhpLauncher.git
cd PhpLauncher
dotnet workload restore
dotnet build PhpLauncher/PhpLauncher.csproj -c Release
```

**Windows** (build on Windows with the Windows TFM enabled in the project):

```bash
dotnet build PhpLauncher/PhpLauncher.csproj -c Release -f net9.0-windows10.0.19041.0
```

**macOS** (Mac Catalyst):

```bash
dotnet build PhpLauncher/PhpLauncher.csproj -c Release -f net9.0-maccatalyst
```

**Linux:** use whichever TFM or publish strategy you have enabled for MAUI/GUI in your environment; if the project does not yet declare a `*-linux` target, align it with Microsoft’s documentation or third-party extensions before you can produce a native executable.

## Configuration

The configuration section is named **`ServerOptions`**. The app currently binds those properties from .NET configuration (mostly **command-line arguments** when starting the executable).

| Property | Description | Default |
|----------|-------------|---------|
| `ProjectPath` | Absolute path to the **Laravel project root** (where `artisan`, `composer.json`, etc. live). | *(empty; must be set)* |
| `FrankenPhpPath` | Folder containing the FrankenPHP binary (`frankenphp` / `frankenphp.exe`). | *(empty; must be set)* |
| `HttpPort` | HTTP port written into the generated `Caddyfile`. | `8080` |
| `HttpsPort` | HTTPS port; the WebView navigates to `https://localhost:{HttpsPort}`. | `8443` |

### `dotnet run` examples

Replace paths and TFMs with yours. Application arguments go **after** `--`.

**Windows (CMD)** — line continuation with `^`:

```bash
dotnet run --project PhpLauncher/PhpLauncher.csproj -f net9.0-windows10.0.19041.0 -- ^
  --ServerOptions:ProjectPath=C:\path\to\my-laravel ^
  --ServerOptions:FrankenPhpPath=C:\path\to\frankenphp-windows-x86_64 ^
  --ServerOptions:HttpsPort=8443 ^
  --ServerOptions:HttpPort=8080
```

**Windows (PowerShell)** — quotes if paths contain spaces:

```powershell
dotnet run --project PhpLauncher/PhpLauncher.csproj -f net9.0-windows10.0.19041.0 -- `
  --ServerOptions:ProjectPath="C:\path\to\my-laravel" `
  --ServerOptions:FrankenPhpPath="C:\path\to\frankenphp-windows-x86_64"
```

**macOS (bash)** — continuation with `\` and POSIX paths:

```bash
dotnet run --project PhpLauncher/PhpLauncher.csproj -f net9.0-maccatalyst -- \
  --ServerOptions:ProjectPath=/Users/you/projects/my-laravel \
  --ServerOptions:FrankenPhpPath=/Users/you/bin/frankenphp \
  --ServerOptions:HttpsPort=8443 \
  --ServerOptions:HttpPort=8080
```

**Linux (bash):** same `--ServerOptions:...` with POSIX paths; `-f` depends on the desktop target in your `.csproj` (for example if you add a `linux` TFM or whatever your MAUI toolchain uses). Without a Linux TFM in the project, you must first be able to compile the launcher for that platform.

If you run a **published binary** (`.exe`, `.app`, etc., depending on the platform), pass the same `--ServerOptions:...` flags to the launcher.

### Environment variables (optional)

If you add `AddEnvironmentVariables()` or an `appsettings.json` in code, you can use the usual .NET pattern, e.g. `ServerOptions__ProjectPath` and `ServerOptions__FrankenPhpPath` (double underscore). As shipped, prefer command-line arguments.

## Important behavior

1. **`Caddyfile` overwrite:** on each start, a `Caddyfile` is generated (or overwritten) at the **Laravel project root** from `ProjectPath`, with the configured ports. Do not hand-edit that file if you expect changes to persist across runs.
2. **Health endpoint:** the service checks `https://localhost:{HttpsPort}/up`. Your Laravel app must respond there (for example with Laravel’s [health route](https://laravel.com/docs/deployment#health-routing) or equivalent) so startup treats the server as ready.
3. **Local HTTPS:** Caddy/FrankenPHP typically uses local certificates; the WebView may show trust warnings depending on the OS and certificate policy.

## Flow (summary)

1. Validate that `ProjectPath` exists.
2. Write the `Caddyfile` into the Laravel project.
3. Run `frankenphp run` with working directory = `ProjectPath`.
4. Wait until the service responds on `/up`.
5. The MAUI UI loads `https://localhost:{HttpsPort}` in the WebView.

## Troubleshooting

| Symptom | What to check |
|---------|----------------|
| “Project path does not exist” | `ProjectPath` is correct and accessible. |
| FrankenPHP exits immediately | `FrankenPhpPath`, execute permissions, and CPU architecture (x64/ARM) match the binary. |
| WebView does not load | `HttpsPort`, firewall, `/up` returns 200, Laravel serves correctly from `public/`. |
| Error writing Caddy logs | `storage/logs` exists in the Laravel project and is writable. |

## Before publishing to GitHub

Add a typical **`.gitignore`** for .NET/MAUI (`bin/`, `obj/`, `.vs/`, publish artifacts) so you do not commit build output or caches. If you bundle FrankenPHP next to the executable only locally, avoid committing large runtime folders unless that is an explicit part of your release strategy.

## License

This project is released under the [MIT License](LICENSE). See the `LICENSE` file in the repository root.

If you are the copyright holder and prefer your name or your organization instead of “PhpLauncher contributors”, replace that line in `LICENSE`.

## Contributing

Improvements are welcome: issues and pull requests help keep the project useful for more scenarios (extra configuration sources, other Caddy templates, etc.).
