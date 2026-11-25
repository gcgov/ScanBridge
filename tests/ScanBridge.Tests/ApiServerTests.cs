using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ScanBridge.Hosting;
using ScanBridge.Scanning;
using ScanBridge.Settings;
using Serilog;
using Xunit;

namespace ScanBridge.Tests;

/// <summary>
/// End-to-end tests of the real Kestrel host (<see cref="WebHostRunner"/>) on a free
/// loopback port, with a fake scan executor instead of a physical scanner.
/// </summary>
public sealed class ApiServerTests : IAsyncLifetime
{
    private const string AllowedOrigin = "https://apps.example.gov";
    private const string OtherOrigin = "https://evil.example.com";

    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] FakeJpeg2 = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x20];
    private static readonly byte[] FakePdf = Encoding.ASCII.GetBytes("%PDF-1.4 fake");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ScanBridgeApiTests-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private int _port;
    private SettingsStore _settingsStore = null!;
    private ScannerService _scannerService = null!;
    private ScanJobManager _jobManager = null!;
    private ScanRequest? _lastRequest;
    private WebHostRunner _runner = null!;

    private string BaseUrl => $"http://127.0.0.1:{_port}/api/v1";

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        _settingsStore = new SettingsStore(_tempDir);
        _settingsStore.Load();
        _settingsStore.Save(new AppSettings
        {
            Port = _port,
            AllowedOrigins = [AllowedOrigin],
            ScannerDriver = "wia",
            ScannerDeviceId = "dev-1",
            ScannerDeviceName = "Test Scanner",
        });

        _scannerService = new ScannerService(NullLogger.Instance);
        _jobManager = new ScanJobManager((request, _, _) =>
        {
            _lastRequest = request;
            // A two-page scan: as one PDF, or as one JPEG per page.
            return Task.FromResult(request.Format == ScanFormats.Jpg
                ? new ScanOutput(ScanFormats.Jpg, [FakeJpeg, FakeJpeg2])
                : ScanOutput.Pdf(FakePdf));
        });
        _runner = new WebHostRunner(_settingsStore, _scannerService, _jobManager, new LoggerConfiguration().CreateLogger());

        await _runner.StartAsync();
        Assert.True(_runner.IsRunning, _runner.LastError?.ToString());
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _runner.DisposeAsync();
        _jobManager.Dispose();
        _scannerService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? body = null, string? origin = null, string? language = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        if (language is not null)
        {
            request.Headers.Add("Accept-Language", language);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task<string> StartScanAsync(string body)
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", body));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("jobId").GetString()!;
    }

    private async Task WaitUntilCompletedAsync(string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "scan did not complete in time");
            using var json = JsonDocument.Parse(await _http.GetStringAsync($"{BaseUrl}/scans/{jobId}"));
            if (json.RootElement.GetProperty("status").GetString() == "completed")
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    // ---- Origin check on POST/DELETE (no custom header needed) ----

    [Fact]
    public async Task PostWithoutOriginIsAccepted()
    {
        // Same-machine tools (curl, desktop apps) send no Origin header.
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", "{}"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostFromAllowedOriginIsAccepted()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", "{}", origin: AllowedOrigin));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostFromAllowedOriginWithTheOldCustomHeaderIsAccepted()
    {
        // Clients written against an earlier draft send X-ScanBridge; it must keep working.
        var request = Request(HttpMethod.Post, "/scans", "{}", origin: AllowedOrigin);
        request.Headers.Add("X-ScanBridge", "1");

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostFromUnauthorizedOriginIsRejected()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", "{}", origin: OtherOrigin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("originNotAllowed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SimplePostFromUnauthorizedOriginDoesNotStartAScan()
    {
        // A "simple" request (text/plain, no preflight) is what CORS alone cannot stop.
        var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/scans")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("Origin", OtherOrigin);
        await _http.SendAsync(request);

        // The scanner must still be free: a legitimate request right after is accepted, not 409.
        var jobId = await StartScanAsync("{}");
        Assert.False(string.IsNullOrEmpty(jobId));
    }

    [Fact]
    public async Task DeleteFromUnauthorizedOriginIsRejected()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Delete, "/scans/anything", origin: OtherOrigin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteWithoutOriginIsAccepted()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Delete, "/scans/anything"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- Host validation (DNS rebinding) ----

    [Theory]
    [InlineData("evil.com")]
    [InlineData("evil.com:1234")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:1")]
    public async Task InvalidHostIsRejected(string host)
    {
        var request = Request(HttpMethod.Get, "/status");
        request.Headers.Host = host;

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LocalhostHostWithPortIsAccepted()
    {
        var request = Request(HttpMethod.Get, "/status");
        request.Headers.Host = $"localhost:{_port}";

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- CORS / origin allowlist ----

    [Fact]
    public async Task AllowedOriginGetsCorsHeader()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/status", origin: AllowedOrigin));

        Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task UnauthorizedOriginGetsNoCorsHeader()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/status", origin: OtherOrigin));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task PreflightFromAllowedOriginAllowsTheCustomHeader()
    {
        var request = Request(HttpMethod.Options, "/scans", origin: AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-scanbridge,content-type");

        var response = await _http.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"preflight returned {(int)response.StatusCode}");
        Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("x-scanbridge", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreflightFromUnauthorizedOriginIsNotAllowed()
    {
        var request = Request(HttpMethod.Options, "/scans", origin: OtherOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-scanbridge,content-type");

        var response = await _http.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- Output format ----

    [Fact]
    public async Task JpgFormatReturnsAJpegDocument()
    {
        var jobId = await StartScanAsync("""{"format":"jpg"}""");
        await WaitUntilCompletedAsync(jobId);

        var response = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public async Task DefaultFormatIsPdf()
    {
        var jobId = await StartScanAsync("{}");
        await WaitUntilCompletedAsync(jobId);

        var response = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document");

        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task OmittedFormatIsAlwaysPdf()
    {
        // A page written before JPG support never sends "format" and must get a PDF.
        await WaitUntilCompletedAsync(await StartScanAsync("""{"dpi":200}"""));

        Assert.Equal(ScanFormats.Pdf, _lastRequest!.Format);
    }

    [Theory]
    [InlineData("JPG")]
    [InlineData("jpeg")]
    [InlineData(" Jpeg ")]
    public async Task JpegSpellingsAreAccepted(string format)
    {
        await WaitUntilCompletedAsync(await StartScanAsync($$"""{"format":"{{format}}"}"""));

        Assert.Equal(ScanFormats.Jpg, _lastRequest!.Format);
    }

    [Fact]
    public async Task JobReportsFormatAndFileCount()
    {
        var jobId = await StartScanAsync("""{"format":"jpg"}""");
        await WaitUntilCompletedAsync(jobId);

        using var json = JsonDocument.Parse(await _http.GetStringAsync($"{BaseUrl}/scans/{jobId}"));
        Assert.Equal("jpg", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("fileCount").GetInt32());
    }

    [Fact]
    public async Task EveryJpgPageCanBeDownloaded()
    {
        var jobId = await StartScanAsync("""{"format":"jpg"}""");
        await WaitUntilCompletedAsync(jobId);

        var first = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document?page=1");
        var second = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document?page=2");

        Assert.Equal(FakeJpeg, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal(FakeJpeg2, await second.Content.ReadAsByteArrayAsync());
        Assert.Equal("scan-2.jpg", second.Content.Headers.ContentDisposition?.FileName);
    }

    [Theory]
    [InlineData("""{"format":"jpg"}""", 3)]
    [InlineData("""{"format":"jpg"}""", 0)]
    [InlineData("{}", 2)]
    public async Task PageOutOfRangeIsNotFound(string body, int page)
    {
        var jobId = await StartScanAsync(body);
        await WaitUntilCompletedAsync(jobId);

        var response = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document?page={page}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("pageNotFound", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownFormatIsRejected()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", """{"format":"png"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalidFormat", await response.Content.ReadAsStringAsync());
    }

    // ---- JPEG quality ----

    [Fact]
    public async Task JpegQualityDefaultsToTheSettingsValue()
    {
        var settings = _settingsStore.Current.Clone();
        settings.Defaults.JpegQuality = 60;
        _settingsStore.Save(settings);

        await WaitUntilCompletedAsync(await StartScanAsync("""{"format":"jpg"}"""));

        Assert.Equal(60, _lastRequest!.JpegQuality);
    }

    [Fact]
    public async Task InvalidJpegQualityInTheSettingsDoesNotBreakScans()
    {
        var settings = _settingsStore.Current.Clone();
        settings.Defaults.JpegQuality = 0;
        _settingsStore.Save(settings);

        await WaitUntilCompletedAsync(await StartScanAsync("""{"format":"jpg"}"""));

        Assert.Equal(ScanFormats.DefaultJpegQuality, _lastRequest!.JpegQuality);
    }

    [Fact]
    public async Task RequestJpegQualityOverridesTheSettings()
    {
        await WaitUntilCompletedAsync(await StartScanAsync("""{"format":"jpg","jpegQuality":40}"""));

        Assert.Equal(40, _lastRequest!.JpegQuality);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public async Task OutOfRangeJpegQualityIsRejected(int quality)
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", $$"""{"format":"jpg","jpegQuality":{{quality}}}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalidJpegQuality", await response.Content.ReadAsStringAsync());
    }

    // ---- Discard after download ----

    [Fact]
    public async Task DocumentCanBeDownloadedTwiceByDefault()
    {
        var jobId = await StartScanAsync("{}");
        await WaitUntilCompletedAsync(jobId);

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document")).StatusCode);
    }

    [Fact]
    public async Task DeleteAfterDownloadDiscardsTheJob()
    {
        var jobId = await StartScanAsync("""{"format":"jpg"}""");
        await WaitUntilCompletedAsync(jobId);

        var first = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document?deleteAfterDownload=true");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(FakeJpeg, await first.Content.ReadAsByteArrayAsync());

        var second = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // ---- Error message language ----

    [Fact]
    public async Task ErrorMessageIsEnglishWithoutAcceptLanguage()
    {
        var response = await _http.GetAsync($"{BaseUrl}/scans/unknown");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("notFound", error.GetProperty("code").GetString());
        Assert.Equal("Unknown scan job.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ErrorMessageFollowsAcceptLanguage()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/scans/unknown", language: "it-IT,it;q=0.9,en;q=0.8"));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("notFound", error.GetProperty("code").GetString());
        Assert.Equal("Scansione sconosciuta.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnsupportedAcceptLanguageFallsBackToEnglish()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/scans/unknown", language: "de-DE"));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Unknown scan job.", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }
}
