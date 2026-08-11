using System.Threading;

namespace ClipboardQueue;

/// <summary>
/// Counts user input events (keys and mouse clicks).
/// This lets us tell whether a clipboard read was caused by a real user
/// paste action, or by a background app (clipboard history, translators, etc.).
/// </summary>
internal static class InputActivity
{
    private static long _count;

    public static void Note()
    {
        Interlocked.Increment(ref _count);
    }

    public static long Count
    {
        get { return Interlocked.Read(ref _count); }
    }
}
