namespace Companion.Core;

public sealed record WindowInfo(
    long Hwnd,
    string Title,
    string ProcessName,
    int X,
    int Y,
    int Width,
    int Height
);
