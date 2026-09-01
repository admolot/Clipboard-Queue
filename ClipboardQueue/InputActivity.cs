using System;
using System.Threading;

namespace ClipboardQueue;

internal static class InputActivity
{
    private static long _count;
    private static long _lastGestureTicks = DateTime.MinValue.Ticks;
    private static long _lastRightButtonUpTicks = DateTime.MinValue.Ticks;
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

    public static void NoteRightButtonUp()
    {
        Volatile.Write(ref _lastRightButtonUpTicks, DateTime.UtcNow.Ticks);
    }

    public static DateTime LastGesture
    {
        get { return new DateTime(Volatile.Read(ref _lastGestureTicks), DateTimeKind.Utc); }
    }

    public static DateTime LastRightButtonUp
    {
        get { return new DateTime(Volatile.Read(ref _lastRightButtonUpTicks), DateTimeKind.Utc); }
    }

    public static uint LastGestureSeq
    {
        get { return _lastGestureSeq; }
    }
}
