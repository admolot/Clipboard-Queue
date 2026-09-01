using System;
using System.Threading;

namespace ClipboardQueue;

/// <summary>
/// Tracks user input, and specifically "paste-like gestures"
/// (Ctrl+V key press, or choosing an item from a right-click menu).
/// Also remembers the clipboard sequence at the moment of the gesture, so
/// the app can tell whether the clipboard changed between gesture and read.
/// </summary>
internal static class InputActivity
{
    private static long _count;
    private static long _lastGestureTicks = DateTime.MinValue.Ticks;
    private static uint _lastGestureSeq;

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
        _lastGestureSeq = NativeMethods.GetClipboardSequenceNumber();
        Volatile.Write(ref _lastGestureTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _count);
    }

    public static DateTime LastGesture
    {
        get { return new DateTime(Volatile.Read(ref _lastGestureTicks), DateTimeKind.Utc); }
    }

    public static uint LastGestureSeq
    {
        get { return _lastGestureSeq; }
    }
}
