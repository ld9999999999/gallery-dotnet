namespace Gallery.Desktop.ViewModels;

/// <summary>What the view models need from the operating system. The main window provides it (fakes do in tests).</summary>
public interface IDesktopShell
{
    /// <summary>A folder chosen in the system dialog, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startPath);

    /// <summary>A file chosen in the system dialog, or null if cancelled.</summary>
    Task<string?> PickFileAsync(string title, string? startPath);

    /// <summary>Where to save a new file, from the system dialog (starting with the suggested name), or null if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string? startPath);

    /// <summary>Asks a question with the given buttons. Returns the chosen button's index, or -1 if the dialog is closed.</summary>
    Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> buttons);

    /// <summary>Opens a file in its default application (videos and audio play there).</summary>
    Task OpenFileAsync(string path);

    /// <summary>Opens the folder containing a file in the file manager.</summary>
    Task RevealAsync(string path);

    /// <summary>Opens a web address in the default browser (a photo's location on a map).</summary>
    Task OpenUrlAsync(Uri url);

    /// <summary>The window's longer side in device pixels, used to pick the viewer's preview size.</summary>
    double LongestSidePixels { get; }
}
