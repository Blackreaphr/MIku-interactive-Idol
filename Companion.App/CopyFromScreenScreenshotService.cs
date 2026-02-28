using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Companion.Core;

namespace Companion.App;

public sealed class CaptureDisabledException : InvalidOperationException
{
    public CaptureDisabledException()
        : base("Capture disabled.")
    {
    }
}

public sealed class CaptureRateLimitedException : InvalidOperationException
{
    public CaptureRateLimitedException(TimeSpan retryAfter)
        : base($"Rate-limited. Retry after {retryAfter.TotalMilliseconds:0} ms.")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}

internal sealed class CopyFromScreenScreenshotService : IScreenshotService
{
    private readonly TimeGateLimiter _limiter;
    private readonly Func<bool> _enabled;

    public CopyFromScreenScreenshotService(TimeGateLimiter limiter, Func<bool> enabled)
    {
        _limiter = limiter;
        _enabled = enabled;
    }

    public Task<byte[]> CapturePngAsync(CaptureTarget target, CancellationToken ct)
    {
        if (target != CaptureTarget.VirtualDesktop)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Unsupported capture target.");
        }

        ct.ThrowIfCancellationRequested();

        if (!_enabled())
        {
            throw new CaptureDisabledException();
        }

        if (!_limiter.TryAcquire(DateTimeOffset.UtcNow, out var retryAfter))
        {
            throw new CaptureRateLimitedException(retryAfter);
        }

        var bounds = SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bitmap.Size,
                CopyPixelOperation.SourceCopy);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Task.FromResult(stream.ToArray());
    }
}
