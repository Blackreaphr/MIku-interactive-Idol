using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MikuAgentBridge.Config;

namespace MikuAgentBridge.Actions;

public sealed class CaptureDisabledException : InvalidOperationException
{
    public CaptureDisabledException()
        : base("Capture is disabled.")
    {
    }
}

public sealed class CaptureRateLimitedException : InvalidOperationException
{
    public CaptureRateLimitedException(int retryAfterMs)
        : base($"Capture is rate-limited. Retry in {retryAfterMs} ms.")
    {
        RetryAfterMs = retryAfterMs;
    }

    public int RetryAfterMs { get; }
}

public sealed record CaptureResult(
    string ReturnMode,
    string? Path,
    string? Base64,
    int ByteLength,
    bool FallbackToPath);

public sealed class ScreenshotService
{
    private readonly Func<Settings> _getSettings;
    private readonly string _capturesPath;
    private readonly object _rateSync = new();

    private DateTimeOffset _lastCaptureAtUtc = DateTimeOffset.MinValue;

    public ScreenshotService(Func<Settings> getSettings, string capturesPath)
    {
        _getSettings = getSettings;
        _capturesPath = capturesPath;
    }

    public Task<CaptureResult> CaptureVirtualDesktopAsync(string returnMode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _getSettings();
        if (!settings.CaptureEnabled)
        {
            throw new CaptureDisabledException();
        }

        EnforceRateLimit(settings.CaptureMinIntervalMs);

        var pngBytes = CaptureVirtualDesktopPng();
        var wantsBase64 = string.Equals(returnMode, "base64", StringComparison.OrdinalIgnoreCase);

        if (wantsBase64 && pngBytes.Length <= settings.MaxInlineCaptureBytes)
        {
            return Task.FromResult(new CaptureResult(
                ReturnMode: "base64",
                Path: null,
                Base64: Convert.ToBase64String(pngBytes),
                ByteLength: pngBytes.Length,
                FallbackToPath: false));
        }

        Directory.CreateDirectory(_capturesPath);
        var outputPath = Path.Combine(_capturesPath, $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        File.WriteAllBytes(outputPath, pngBytes);

        return Task.FromResult(new CaptureResult(
            ReturnMode: "path",
            Path: outputPath,
            Base64: null,
            ByteLength: pngBytes.Length,
            FallbackToPath: wantsBase64));
    }

    private void EnforceRateLimit(int minIntervalMs)
    {
        lock (_rateSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastCaptureAtUtc != DateTimeOffset.MinValue)
            {
                var elapsed = now - _lastCaptureAtUtc;
                var minInterval = TimeSpan.FromMilliseconds(minIntervalMs);
                if (elapsed < minInterval)
                {
                    var retryAfter = minInterval - elapsed;
                    throw new CaptureRateLimitedException((int)Math.Ceiling(retryAfter.TotalMilliseconds));
                }
            }

            _lastCaptureAtUtc = now;
        }
    }

    private static byte[] CaptureVirtualDesktopPng()
    {
        var left = GetSystemMetrics(SmXvirtualscreen);
        var top = GetSystemMetrics(SmYvirtualscreen);
        var width = GetSystemMetrics(SmCxvirtualscreen);
        var height = GetSystemMetrics(SmCyvirtualscreen);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Virtual desktop metrics are invalid.");
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC failed.");
        }

        var memDc = IntPtr.Zero;
        var bitmapHandle = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            bitmapHandle = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmapHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleBitmap failed.");
            }

            previous = SelectObject(memDc, bitmapHandle);
            if (previous == IntPtr.Zero)
            {
                throw new InvalidOperationException("SelectObject failed.");
            }

            var bltOk = BitBlt(
                memDc,
                0,
                0,
                width,
                height,
                screenDc,
                left,
                top,
                Srccopy | Captureblt);

            if (!bltOk)
            {
                throw new InvalidOperationException("BitBlt failed.");
            }

            using var image = Image.FromHbitmap(bitmapHandle);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        finally
        {
            if (previous != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                _ = SelectObject(memDc, previous);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                _ = DeleteObject(bitmapHandle);
            }

            if (memDc != IntPtr.Zero)
            {
                _ = DeleteDC(memDc);
            }

            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = unchecked((int)0x40000000);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int nXDest,
        int nYDest,
        int nWidth,
        int nHeight,
        IntPtr hdcSrc,
        int nXSrc,
        int nYSrc,
        int dwRop);
}
