using System;
using System.Threading;

namespace ClipboardQueue;

/// <summary>
/// Tracks user input, and specifically "paste-like gestures"
/// (Ctrl+V key press, or choosing an item from a right-click menu).
/// This lets us tell a real paste apart from apps that silently read
/// the clipboard (e.g. Anki's Add window).
/// </summary>
internal static class InputActivity
{
    private static long _count;
    private static long _lastGestureTicks = DateTime.MinValue.Ticks;

    public static void Note()
    {
        Interlocked.Increment(ref _count);
    }

    public static long Count
    {
        get { return Interlocked.Read(ref _count); }
    }

    public static void NoteGesture()
    {
        Volatile.Write(ref _lastGestureTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _count);
    }

    public static DateTime LastGesture
    {
        get { return new DateTime(Volatile.Read(ref _lastGestureTicks), DateTimeKind.Utc); }
    }
}
