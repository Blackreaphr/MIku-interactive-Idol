namespace Companion.App;

public enum CaptureTarget
{
    VirtualDesktop
}

public interface IScreenshotService
{
    Task<byte[]> CapturePngAsync(CaptureTarget target, CancellationToken ct);
}
