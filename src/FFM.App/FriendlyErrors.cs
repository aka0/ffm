using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;

namespace FFM.App;

internal static class FriendlyErrors
{
    public static string Message(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            var failures = aggregate.Flatten().InnerExceptions;
            foreach (var failure in failures) Log(failure);
            if (failures.Count >= 2)
                return $"Portrait acceptance failed: {SimpleMessage(failures[0])} Automatic recovery also failed: {SimpleMessage(failures[1])} The transaction journal and numbered backup were retained; use Retry recovery.";
            return string.Join(" ", failures.Select(SimpleMessage).Distinct());
        }
        Log(error);
        return SimpleMessage(error);
    }

    private static string SimpleMessage(Exception error) => error switch
    {
        OperationCanceledException => "The operation was cancelled.",
        InvalidDataException => error.Message,
        InvalidOperationException => error.Message,
        FileFormatException => "This image or save format could not be read. Choose a supported file and try again.",
        UnauthorizedAccessException => "FFM cannot access this file or folder. Check its permissions and try again.",
        IOException when error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) => "Football Manager or another program is using this portrait. Close it and try again.",
        COMException when error.HResult == unchecked((int)0x800401D0) => "The Windows clipboard is busy. Wait a moment and try again.",
        COMException => "Windows could not access the clipboard. Wait a moment and try again.",
        IOException => "A file could not be read or written. Check that it is available, then try again.",
        ArgumentException or NotSupportedException => "The selected path or file is not supported.",
        _ => "The operation could not be completed. Details were saved in the local FFM log folder."
    };

    private static void Log(Exception error)
    {
        if (Application.Current is App app) app.LogException("Handled operation", error);
    }
}
