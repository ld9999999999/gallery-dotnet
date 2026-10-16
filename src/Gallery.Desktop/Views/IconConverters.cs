using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Gallery.Desktop.Views;

/// <summary>Turns the viewer's placeholder names ("note", "play") into icon shapes from the app resources.</summary>
public static class IconConverters
{
    public static readonly IValueConverter ByName = new FuncValueConverter<string?, Geometry?>(name => name switch
    {
        "note" => Find("IconNote"),
        "play" => Find("IconPlay"),
        _ => null,
    });

    public static readonly IValueConverter IsFilled = new FuncValueConverter<string?, bool>(name => name == "play");

    private static Geometry? Find(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;
}
