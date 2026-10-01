# ScanBridge

A small Windows tray application that lets **web applications use desktop document
scanners**. A page in the browser calls ScanBridge's local HTTP API; ScanBridge scans
from the configured scanner (WIA, TWAIN or ESCL, via the excellent
[NAPS2](https://www.naps2.com/) SDK) and hands the result back to the page as a PDF.

ScanBridge is generic — it knows nothing about any particular web app. Any site whose
origin an administrator adds to the allowlist can trigger scans.

```
┌──────────────┐  fetch http://127.0.0.1:7226   ┌────────────┐   WIA/TWAIN/ESCL   ┌─────────┐
│ Your web app │ ─────────────────────────────► │ ScanBridge │ ─────────────────► │ Scanner │
│  (browser)   │ ◄───────────── PDF ─────────── │ (tray app) │ ◄──── pages ────── │         │
└──────────────┘                                └────────────┘                    └─────────┘
```

## Install

Grab the latest release from the [Releases](../../releases) page:

- **`ScanBridge-<version>-setup.exe`** — per-user installer (no admin rights needed).
  Installs to `%LocalAppData%\Programs\ScanBridge` and starts at login by default.
- **`ScanBridge-<version>-win-x64-portable.zip`** — portable build; unzip anywhere and
  run `ScanBridge.exe`.

Both are self-contained: no .NET runtime install is required.

Intune-managed computers get each new release automatically. See
[docs/intune.md](docs/intune.md) for the setup.

On first run the settings window opens. Pick your scanner, set the scan defaults, and —
important — add the website origin(s) that are allowed to use the scanner.

## Configuration

Right-click the tray icon → **Settings…**

| Setting | Meaning |
|---|---|
| Driver / Scanner | WIA (most Windows scanners), TWAIN (older drivers), or ESCL (network/AirScan, driverless). Refresh re-enumerates. |
| Scan defaults | Paper source (flatbed / feeder / duplex), resolution, color mode, page size, blank-page removal, deskew. Web apps can override any of these per request. |
| Port | The local HTTP port (default **7226**). ScanBridge listens on `127.0.0.1` only — it is never reachable from the network. |
| Allowed website origins | Exact origins (e.g. `https://apps.example.gov`) allowed to call the API from a browser. Empty list = no site may use it. |
| Start when I sign in | Per-user autostart (registry `Run` key). |

Settings live in `%AppData%\ScanBridge\settings.json`; logs in
`%LocalAppData%\ScanBridge\logs`.

## Using it from a web page

See [docs/api.md](docs/api.md) for the full HTTP API. The short version:

```js
// 1. start a scan
const startRes = await fetch('http://127.0.0.1:7226/api/v1/scans', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({}),                 // {} = use the configured defaults
  targetAddressSpace: 'loopback',           // Local Network Access hint (Chrome 142+)
});
const { jobId } = await startRes.json();

// 2. poll until done
let job;
do {
  await new Promise(r => setTimeout(r, 1000));
  job = await (await fetch(`http://127.0.0.1:7226/api/v1/scans/${jobId}`,
    { targetAddressSpace: 'loopback' })).json();
} while (!['completed', 'failed', 'canceled'].includes(job.status));

// 3. fetch the PDF
const pdfBlob = await (await fetch(`http://127.0.0.1:7226/api/v1/scans/${jobId}/document`,
  { targetAddressSpace: 'loopback' })).blob();
```

### Browser requirements (Local Network Access)

Calling `http://127.0.0.1` from an HTTPS page is allowed by Chrome, Edge and Firefox
(loopback is a "potentially trustworthy" origin; Safari currently blocks it). Since
Chrome 142, the **Local Network Access** feature additionally shows a one-time
permission prompt the first time a site talks to the local machine; the user must click
**Allow**. The decision is remembered per site.

For managed fleets, administrators can skip the prompt entirely by adding the web app's
origin to the `LocalNetworkAccessAllowedForUrls` enterprise policy (Chrome/Edge via
GPO or Intune; Firefox has an equivalent `LocalNetworkAccess` policy).

### Security model

- The listener binds to `127.0.0.1` only — nothing on the network can reach it.
- Browsers enforce CORS: only origins on the allowlist get responses.
- Requests carry no credentials and ScanBridge stores no secrets; the worst a malicious
  allowed page could do is trigger a scan on the attached scanner.

## Building from source

Requires the .NET 10 SDK on Windows.

```
dotnet build ScanBridge.sln
dotnet test ScanBridge.sln
dotnet publish src/ScanBridge -c Release -r win-x64 --self-contained true
```

The publish output is a plain folder — that is deliberate (see licensing below). The
installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) from
`installer/ScanBridge.iss`.

### Manual smoke test

1. Run `ScanBridge.exe`, pick a scanner in Settings, and add `https://localhost:8097`
   (or your dev origin) to the allowed origins.
2. `curl http://127.0.0.1:7226/api/v1/status` → JSON status.
3. Tray menu → **Scan test page** → your PDF viewer opens the scanned page.
4. From a disallowed origin, a browser `fetch` must fail CORS.

## Licensing

ScanBridge is MIT-licensed (see [LICENSE](LICENSE)).

It depends on [NAPS2.Sdk](https://www.nuget.org/packages/NAPS2.Sdk), which is
**LGPL-2.1**. ScanBridge consumes NAPS2.Sdk as unmodified assemblies and is published
as a plain folder (no trimming or merging), so you can swap in your own build of the
LGPL libraries. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
