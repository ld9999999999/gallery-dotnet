using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Gallery.Develop;

namespace Gallery.Desktop.ViewModels;

/// <summary>One adjustment slider. It reads its value from the settings and writes a changed value into a copy of them.</summary>
public sealed partial class SliderViewModel : ObservableObject
{
    private readonly Func<DevelopSettings, double> _get;
    private readonly Func<DevelopSettings, double, DevelopSettings> _set;
    private readonly Func<double, string> _format;
    private readonly Action<SliderViewModel, double> _changed;
    private bool _syncing;

    internal SliderViewModel(string label, double min, double max, double step, Func<DevelopSettings, double> get,
        Func<DevelopSettings, double, DevelopSettings> set, Func<double, string> format, Action<SliderViewModel, double> changed)
    {
        (Label, Minimum, Maximum, Step) = (label, min, max, step);
        (_get, _set, _format, _changed) = (get, set, format, changed);
    }

    public string Label { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    /// <summary>A bigger step for Page Up and Page Down: a tenth of the range.</summary>
    public double LargeStep => (Maximum - Minimum) / 10;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text))] private double _value;

    public string Text => _format(Value);

    partial void OnValueChanged(double value)
    {
        if (!_syncing) _changed(this, value);
    }

    /// <summary>Shows the value from these settings without counting it as a change.</summary>
    internal void Sync(DevelopSettings settings)
    {
        _syncing = true;
        try { Value = _get(settings); }
        finally { _syncing = false; }
    }

    /// <summary>Returns the settings with this slider set to <paramref name="value"/>, rounded to its step.</summary>
    internal DevelopSettings Apply(DevelopSettings settings, double value) => _set(settings, Rounded(value));

    /// <summary>Rounds the value to the slider's step and keeps it within its range.</summary>
    internal double Rounded(double value) => Math.Clamp(Math.Round(Math.Round(value / Step) * Step, 4), Minimum, Maximum);
}

/// <summary>A titled group of sliders in the editor's panel.</summary>
public sealed record SliderGroup(string Title, IReadOnlyList<SliderViewModel> Sliders);

/// <summary>The editor's sliders, with the same groups and ranges as the web editor (wwwroot/editor.js).</summary>
internal static class EditorSliders
{
    private static string Signed(double v) => (v > 0 ? "+" : "") + Math.Round(v).ToString(CultureInfo.CurrentCulture);
    private static string Whole(double v) => Math.Round(v).ToString(CultureInfo.CurrentCulture);

    public static IReadOnlyList<SliderGroup> Build(Action<SliderViewModel, double> changed)
    {
        SliderViewModel S(string label, double min, double max, double step, Func<DevelopSettings, double> get,
            Func<DevelopSettings, double, DevelopSettings> set, Func<double, string>? format = null) =>
            new(label, min, max, step, get, set, format ?? Signed, changed);

        return
        [
            new("White balance",
            [
                S("Temperature", -100, 100, 1, s => s.WhiteBalance.Temperature, (s, v) => s with { WhiteBalance = s.WhiteBalance with { Temperature = v } }),
                S("Tint", -100, 100, 1, s => s.WhiteBalance.Tint, (s, v) => s with { WhiteBalance = s.WhiteBalance with { Tint = v } }),
            ]),
            new("Tone",
            [
                S("Exposure", -5, 5, 0.05, s => s.Exposure, (s, v) => s with { Exposure = v }, v => (v > 0 ? "+" : "") + v.ToString("0.00", CultureInfo.CurrentCulture)),
                S("Contrast", -100, 100, 1, s => s.Contrast, (s, v) => s with { Contrast = v }),
                S("Brightness", -100, 100, 1, s => s.Brightness, (s, v) => s with { Brightness = v }),
                S("Highlights", -100, 100, 1, s => s.Highlights, (s, v) => s with { Highlights = v }),
                S("Shadows", -100, 100, 1, s => s.Shadows, (s, v) => s with { Shadows = v }),
            ]),
            new("Colour",
            [
                S("Saturation", -100, 100, 1, s => s.Saturation, (s, v) => s with { Saturation = v }),
                S("Vibrance", -100, 100, 1, s => s.Vibrance, (s, v) => s with { Vibrance = v }),
            ]),
            new("Detail",
            [
                S("Noise reduction", 0, 100, 1, s => s.NoiseReduction.Luminance, (s, v) => s with { NoiseReduction = s.NoiseReduction with { Luminance = v } }, Whole),
                S("Colour noise reduction", 0, 100, 1, s => s.NoiseReduction.Color, (s, v) => s with { NoiseReduction = s.NoiseReduction with { Color = v } }, Whole),
                S("Sharpening", 0, 150, 1, s => s.Sharpening.Amount, (s, v) => s with { Sharpening = s.Sharpening with { Amount = v } }, Whole),
                S("Sharpening radius", 0.5, 3, 0.1, s => s.Sharpening.Radius, (s, v) => s with { Sharpening = s.Sharpening with { Radius = v } },
                    v => v.ToString("0.0", CultureInfo.CurrentCulture)),
                S("Sharpening masking", 0, 100, 1, s => s.Sharpening.Masking, (s, v) => s with { Sharpening = s.Sharpening with { Masking = v } }, Whole),
            ]),
        ];
    }

    /// <summary>
    /// The crop panel's Straighten slider: the picture's rotation in degrees. Changes go to <paramref name="changed"/>,
    /// which also fits the crop frame to the new angle.
    /// </summary>
    public static SliderViewModel Straighten(Action<SliderViewModel, double> changed) =>
        new("Straighten", -45, 45, 0.1, s => s.Crop.Angle, (s, v) => s with { Crop = s.Crop with { Angle = v } },
            v => (v > 0 ? "+" : "") + v.ToString("0.0", CultureInfo.CurrentCulture) + "°", changed);
}
