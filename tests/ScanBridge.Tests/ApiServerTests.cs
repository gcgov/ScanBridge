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
            return Task.FromResult(request.Format == ScanFormats.Jpg ? FakeJpeg : FakePdf);
        });
        _runner = new WebHostRunner(_settingsStore, _scannerService, _jobManager, new LoggerConfiguration().CreateLogger());

        await _runner.StartAsync();
        Assert.True(_runner.IsRunning, _runner.LastError);
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

    private HttpRequestMessage Request(HttpMethod method, string path, string? body = null, bool withHeader = true, string? origin = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        if (withHeader)
        {
            request.Headers.Add("X-ScanBridge", "1");
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

    // ---- Mandatory X-ScanBridge header ----

    [Fact]
    public async Task PostWithoutHeaderIsRejected()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", "{}", withHeader: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostWithoutHeaderDoesNotStartAScan()
    {
        await _http.SendAsync(Request(HttpMethod.Post, "/scans", "{}", withHeader: false));

        // The scanner must still be free: a legitimate request right after is accepted, not 409.
        var jobId = await StartScanAsync("{}");
        Assert.False(string.IsNullOrEmpty(jobId));
    }

    [Fact]
    public async Task PostWithHeaderIsAccepted()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "/scans", "{}"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task DeleteWithoutHeaderIsRejected()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Delete, "/scans/anything", withHeader: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteWithHeaderIsAccepted()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Delete, "/scans/anything"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetDoesNotNeedTheHeader()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/status", withHeader: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        var request = Request(HttpMethod.Get, "/status", withHeader: false);
        request.Headers.Host = host;

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LocalhostHostWithPortIsAccepted()
    {
        var request = Request(HttpMethod.Get, "/status", withHeader: false);
        request.Headers.Host = $"localhost:{_port}";

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- CORS / origin allowlist ----

    [Fact]
    public async Task AllowedOriginGetsCorsHeader()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/status", withHeader: false, origin: AllowedOrigin));

        Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task UnauthorizedOriginGetsNoCorsHeader()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/status", withHeader: false, origin: OtherOrigin));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task PreflightFromAllowedOriginAllowsTheCustomHeader()
    {
        var request = Request(HttpMethod.Options, "/scans", withHeader: false, origin: AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-scanbridge,content-type");

        var response = await _http.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"preflight returned {(int)response.StatusCode}");
        Assert.Equal(AllowedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("x-scanbridge", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreflightFromUnauthorizedOriginIsNotAllowed()
    {
        var request = Request(HttpMethod.Options, "/scans", withHeader: false, origin: OtherOrigin);
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
    public async Task SettingsFormatIsUsedWhenTheRequestOmitsIt()
    {
        var settings = _settingsStore.Current.Clone();
        settings.Defaults.Format = "jpg";
        _settingsStore.Save(settings);

        var jobId = await StartScanAsync("{}");
        await WaitUntilCompletedAsync(jobId);

        var response = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document");
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RequestFormatOverridesTheSettings()
    {
        var settings = _settingsStore.Current.Clone();
        settings.Defaults.Format = "jpg";
        _settingsStore.Save(settings);

        var jobId = await StartScanAsync("""{"format":"pdf"}""");
        await WaitUntilCompletedAsync(jobId);

        var response = await _http.GetAsync($"{BaseUrl}/scans/{jobId}/document");
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
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
}
