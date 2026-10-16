using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Gallery.Core.Models;
using Gallery.Desktop.ViewModels;

namespace Gallery.Desktop.Views;

public partial class MainWindow : Window, IDesktopShell
{
    public MainWindow()
    {
        InitializeComponent();
        // Use Tunnel (the window sees keys first), so the viewer, editor and gallery keys work whichever control has focus.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm) vm.Shell = this;
    }

    /// <summary>
    /// Viewer keys: Esc closes it. ←/→ go to the previous/next item. F, E and I show faces, the editor and details. [ and ]
    /// rotate. Z switches between fit and 100%, + and − step through the zoom levels, and 0 fits (with or without ⌘/Ctrl,
    /// as in the editor). V switches a 360° photo between the 360° view and flat. In the 360° view, ↑ ↓ and Shift+← → turn,
    /// + − zoom and 0 resets.
    /// </summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainViewModel { Editor: { } editor }) { OnEditorKey(editor, e); return; }
        if (DataContext is not MainViewModel { Viewer: { } v }) { OnGalleryKey(e); return; }
        if (e.Key != Key.Escape && (e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox)) return; // the user is typing a name
        if (v.SphereKey(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift))) { e.Handled = true; return; }
        var command = e.Key switch
        {
            Key.Escape => v.CloseCommand,
            Key.F => v.ToggleFacesCommand,
            Key.E => v.EditCommand,
            Key.Left => v.PreviousCommand,
            Key.Right => v.NextCommand,
            Key.I => v.ToggleInfoCommand,
            Key.OemOpenBrackets => v.RotateLeftCommand,
            Key.OemCloseBrackets => v.RotateRightCommand,
            Key.Z => v.ToggleZoomCommand,
            Key.OemPlus or Key.Add => v.ZoomInCommand,
            Key.OemMinus or Key.Subtract => v.ZoomOutCommand,
            Key.D0 or Key.NumPad0 => v.FitCommand,
            Key.V => v.ToggleSphereCommand,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Page Up and Down, Home and End scroll the gallery on screen (the photo grid, or the albums, years or people), like
    /// a web page, wherever the focus is. Avalonia's scroll views ignore these keys. Skip this while
    /// typing in a text box (Home and End move the caret there) or choosing in a menu.
    /// </summary>
    private void OnGalleryKey(KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown or Key.Home or Key.End) || e.KeyModifiers != KeyModifiers.None) return;
        var focused = FocusManager?.GetFocusedElement();
        if (e.Source is TextBox || focused is TextBox or ComboBox or ComboBoxItem) return;
        if (MainScroller() is not { } scroller) return;
        switch (e.Key)
        {
            case Key.PageUp: scroller.PageUp(); break;
            case Key.PageDown: scroller.PageDown(); break;
            case Key.Home: scroller.ScrollToHome(); break;
            case Key.End: scroller.ScrollToEnd(); break;
        }
        e.Handled = true;
    }

    /// <summary>Finds the largest scroll view on screen that has more than fits: the tab's grid or list, not a side panel.</summary>
    private ScrollViewer? MainScroller() => this.GetVisualDescendants().OfType<ScrollViewer>()
        .Where(s => s.IsEffectivelyVisible && s.Extent.Height > s.Viewport.Height + 1)
        .MaxBy(s => s.Viewport.Width * s.Viewport.Height);

    private static bool IsBackslash(Key key) => key is Key.OemPipe or Key.OemBackslash;

    /// <summary>
    /// Editor keys: Esc closes a panel, leaves crop mode or closes the editor. C turns crop mode on or off, and Enter
    /// leaves it (unless a button or menu has focus). ⌘/Ctrl Z undoes (with Shift, redoes). ⌘/Ctrl S saves. Z turns the
    /// 100% view on or off. ⌘/Ctrl + and − zoom in and out (whole picture, 100%, 200%), and ⌘/Ctrl 0 shows the whole
    /// picture. Holding \ shows Before. While typing in a text box, only Esc works.
    /// </summary>
    private void OnEditorKey(EditorViewModel editor, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _ = editor.EscapeAsync(); e.Handled = true; return; }
        var focused = FocusManager?.GetFocusedElement();
        if (e.Source is TextBox || focused is TextBox) return;
        var mod = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0;
        var menu = focused is ComboBox or ComboBoxItem;
        if (e.Key == Key.Enter && editor.IsCropping && !menu && focused is not Button) { editor.ExitCrop(); e.Handled = true; return; }
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.None && !menu) { editor.ToggleCropCommand.Execute(null); e.Handled = true; return; }
        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.Z when mod && e.KeyModifiers.HasFlag(KeyModifiers.Shift) => editor.RedoCommand,
            Key.Z when mod => editor.UndoCommand,
            Key.Y when mod => editor.RedoCommand,
            Key.S when mod => editor.SaveCommand,
            Key.OemPlus or Key.Add when mod => editor.ZoomInCommand,
            Key.OemMinus or Key.Subtract when mod => editor.ZoomOutCommand,
            Key.D0 or Key.NumPad0 when mod => editor.FitCommand,
            Key.Z => editor.ToggleZoomCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
        else if (IsBackslash(e.Key) && editor.IsLoaded)
        {
            editor.IsComparing = true;
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainViewModel { Editor: { } editor } && IsBackslash(e.Key)) editor.IsComparing = false;
    }

    public double LongestSidePixels => Math.Max(Bounds.Width, Bounds.Height) * RenderScaling;

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string? startPath)
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "jpg",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("JPEG image") { Patterns = ["*.jpg", "*.jpeg"] }],
            SuggestedStartLocation = await StartFolderAsync(startPath),
        };
        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public async Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> buttons)
    {
        var chosen = -1;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Avalonia.Media.IBrush?)this.FindResource("GalleryPanel"),
        };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], IsDefault = i == 0, IsCancel = i == buttons.Count - 1 };
            if (i == 0) button.Classes.Add("accent");
            button.Click += (_, _) => { chosen = index; dialog.Close(); };
            row.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22, 20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 },
                row,
            },
        };
        await dialog.ShowDialog(this);
        return chosen;
    }

    public async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = await StartFolderAsync(startPath) };
        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string? startPath)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = await StartFolderAsync(startPath) };
        var picked = await StorageProvider.OpenFilePickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>Returns the folder a path names, or the folder that holds the file it names, if it exists.</summary>
    private async Task<IStorageFolder?> StartFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = GalleryOptions.ExpandHome(path.Trim());
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        return dir is not null && Directory.Exists(dir) ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
    }

    public Task OpenFileAsync(string path) => Launcher.LaunchFileInfoAsync(new FileInfo(path));

    public Task RevealAsync(string path) => Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetDirectoryName(path) ?? path));

    public Task OpenUrlAsync(Uri url) => Launcher.LaunchUriAsync(url);
}
