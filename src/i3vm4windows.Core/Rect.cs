namespace i3vm4windows.Core;

/// <summary>A rectangle in screen coordinates.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
