# ScanBridge HTTP API

Base URL: `http://127.0.0.1:<port>` — default port **7226**. The listener binds to
loopback only. All request and response bodies are JSON (camelCase) unless noted.

Browsers must be subject to the CORS allowlist: the calling page's origin has to be
listed under *Allowed website origins* in ScanBridge settings. Same-machine tools
(curl, desktop apps) are not subject to CORS.

A `POST` or `DELETE` request under `/api/v1` that carries an `Origin` header not in the
allowlist gets `403` `originNotAllowed`, and no scan starts. CORS alone does not stop a
"simple" cross-origin request (for example a `text/plain` body with no preflight); this
check does. Requests without an `Origin` header, such as curl or a desktop app, are not
affected. A request whose `Host` header is not `127.0.0.1:<port>` or `localhost:<port>`
gets `403` `invalidHost` (DNS rebinding protection). Neither check needs a change in an
existing client.

Requests from browsers should pass `targetAddressSpace: 'loopback'` in the `fetch`
options to satisfy Chrome's Local Network Access rules (Chrome 142+).

## Error shape

Non-2xx responses (except 404s from unknown URLs) use:

```json
{ "error": { "code": "scannerBusy", "message": "A scan is already in progress." } }
```

Program against `code`: its values never change. `message` is text for people. It is in
the language of the request's `Accept-Language` header when ScanBridge has that
language (English and Italian today), and in English otherwise. A request without
`Accept-Language` gets English. Browsers send the user's language automatically, so a
web page can show `message` as it is. The response carries a `Content-Language` header.

| Code | Meaning |
|---|---|
| `scannerBusy` | Another scan job is running; scanners are exclusive. |
| `noScannerConfigured` | No scanner selected in settings and no `scannerId` given. |
| `deviceOffline` | The scanner is unreachable (off, unplugged). |
| `noPages` | The scan produced zero pages (empty feeder). |
| `canceled` | The job was canceled. |
| `scanFailed` | Any other scan failure; `message` has details. |
| `notReady` | Document requested before the job completed. |
| `notFound` | Unknown job id. |
| `pageNotFound` | The `page` asked for is not in the finished scan. |
| `invalidFormat` | `format` is not `pdf` or `jpg`. |
| `invalidJpegQuality` | `jpegQuality` is not between 1 and 100. |
| `originNotAllowed` | A `POST`/`DELETE` came from a website that is not in the allowlist. |
| `invalidHost` | The `Host` header is not ScanBridge's loopback address and port. |

## Endpoints

### `GET /api/v1/status`

Handshake / discovery. Use this to detect whether ScanBridge is installed and running.

```json
{ "app": "ScanBridge", "version": "1.0.0", "apiVersion": 1,
  "scannerConfigured": true, "scannerName": "Canon imageFORMULA R40" }
```

### `GET /api/v1/scanners?driver=wia&refresh=1`

Lists available scanners. `driver` is `wia` (default), `twain` or `escl`; omitting it
uses the configured driver. Results are cached per driver; `refresh=1` re-enumerates.
ESCL enumeration searches the local network and can take a few seconds.

```json
[ { "id": "{6BDD1FC6-...}", "name": "Canon imageFORMULA R40", "driver": "wia" } ]
```

### `POST /api/v1/scans`

Starts a scan job. Body is optional; every field falls back to the configured default:

```json
{
  "scannerId": null,
  "driver": null,
  "paperSource": "feeder",      // "flatbed" | "feeder" | "duplex"
  "duplex": false,               // shorthand for paperSource: "duplex"
  "dpi": 300,
  "colorMode": "color",         // "color" | "grayscale" | "blackAndWhite"
  "pageSize": "letter",         // "letter" | "legal" | "a4"
  "excludeBlankPages": false,
  "autoDeskew": false,
  "format": "pdf",              // "pdf" | "jpg"; omitted means "pdf"
  "jpegQuality": 85             // 1-100, only used with "jpg"
}
```

`format` is new in this version and is optional. Without it, the result is a PDF that
holds every page, as in earlier versions; no setting changes that. With `"format": "jpg"`
(or `"jpeg"`), each scanned page becomes its own JPEG file; see the document endpoint.
`jpegQuality` defaults to the value in the settings window (85).

On a new install, the default `pageSize` is the usual paper size of the Windows region:
`letter` in the United States, Canada, Mexico and the other Letter regions, `a4`
elsewhere. An existing settings file keeps its value.

Responses: `202 Accepted` with `{ "jobId": "…" }` (Location header points at the job),
`400` `invalidFormat` / `invalidJpegQuality`, `409` `scannerBusy`, `422` `noScannerConfigured`.

### `GET /api/v1/scans/{jobId}`

Polls a job.

```json
{ "jobId": "…", "status": "completed", "pagesScanned": 2,
  "format": "jpg", "fileCount": 2, "error": null }
```

`status` is `pending | scanning | processing | completed | failed | canceled`. When
`failed`/`canceled`, `error` carries `{code, message}`. `format` is `pdf` or `jpg`.
`fileCount` is the number of files to download once the job is `completed`: always 1
for a PDF, one per page for JPG, and 0 before completion.

### `GET /api/v1/scans/{jobId}/document`

Returns the finished scan as `application/pdf`, or as `image/jpeg` if the job was
started with `"format": "jpg"`. `409 notReady` while the job is still running; `410`
with the failure code if the job ended without a document; `404` for unknown ids.

`?page=N` (1-based, default 1) picks the file: for a JPG scan, page N of `fileCount`.
A PDF scan has one file, so only `page=1` exists. Any other page returns `404`
`pageNotFound`. Without `page`, a PDF scan returns the PDF, as in earlier versions.

Add `?deleteAfterDownload=true` to discard the job from memory as soon as this file has
been sent (a second download then returns `404`). For a JPG scan with several pages,
add it to the last download only.

### `DELETE /api/v1/scans/{jobId}`

Cancels a running job, or discards a finished one (freeing its files from memory).
Always `204`. Finished jobs are also discarded automatically after 10 minutes.

## Typical client flow

1. `GET /status` — if unreachable, tell the user to install/start ScanBridge.
2. `POST /scans` with `{}`.
3. Poll `GET /scans/{jobId}` every second; show `pagesScanned`.
4. On `completed`, `GET /scans/{jobId}/document` → Blob. For a JPG scan, fetch
   `?page=1` to `?page=<fileCount>`.
5. `DELETE /scans/{jobId}` when done (optional but polite).
