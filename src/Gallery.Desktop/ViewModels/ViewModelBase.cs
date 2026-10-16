using CommunityToolkit.Mvvm.ComponentModel;

namespace Gallery.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>An entry in a virtualized list: loaded when its view is on screen, released when the view goes away or is reused.</summary>
public interface IRealizable
{
    void Realize();
    void Release();
}

/// <summary>A choice in a drop-down: what the user sees and the value the library query uses.</summary>
public sealed record Choice(string Label, string Value);

/// <summary>A label and value in the viewer's info panel.</summary>
public sealed record InfoRow(string Label, string Value);
