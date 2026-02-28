namespace Companion.Core;

public interface IWindowActionService
{
    bool Focus(long hwnd, out string? error);

    bool MoveResize(long hwnd, int x, int y, int width, int height, out string? error);

    bool Nudge(long hwnd, int dx, int dy, out string? error);

    bool Minimize(long hwnd, out string? error);

    bool Maximize(long hwnd, out string? error);

    bool Restore(long hwnd, out string? error);

    bool CloseRequest(long hwnd, out string? error);
}
