namespace Companion.Core;

public interface IWindowQueryService
{
    IReadOnlyList<WindowInfo> ListTopLevelWindows();

    WindowInfo? GetActiveWindow();

    WindowInfo? GetWindowUnderCursor();
}
