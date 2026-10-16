using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gallery.Core.Services.Faces;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>
/// The "People" tab, like the web gallery's People pages. It shows the people list (named first, then suggestions to
/// name) and, for the selected person, their photos or their faces to correct. It can also show the faces nobody is
/// assigned to, each with the person it looks most like. The desktop app runs as the library's owner, so all of this
/// can be corrected here.
/// </summary>
public sealed partial class PeopleViewModel : ViewModelBase
{
    private const int FacePage = 200;
    private readonly LibraryHost _library;
    private readonly Func<IDesktopShell?> _shell;
    private bool _refreshing;

    public PeopleViewModel(LibraryHost library, Func<IDesktopShell?>? shell = null)
    {
        _library = library;
        _shell = shell ?? (() => null);
        Grid = new PhotoGridViewModel(library);
    }

    private IDesktopShell? Shell => _shell();

    public PhotoGridViewModel Grid { get; }
    public ObservableCollection<PersonEntryViewModel> People { get; } = new();
    public ObservableCollection<FaceTileViewModel> Faces { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPerson), nameof(ShowPhotos), nameof(ShowFaceTiles), nameof(NameButton), nameof(DeleteButton), nameof(HideButton), nameof(PersonTitle))]
    private PersonEntryViewModel? _selected;
    /// <summary>The selected person's faces instead of their photos.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPhotos), nameof(ShowFaceTiles))] private bool _showFaces;
    /// <summary>The faces nobody is assigned to, instead of a person.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPhotos), nameof(ShowFaceTiles), nameof(PersonTitle))] private bool _showUnassigned;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string _assignText = "";
    [ObservableProperty] private string _mergeText = "";
    [ObservableProperty] private string _unassignedLabel = "Faces nobody is assigned to";
    [ObservableProperty] private long _faceTotal;
    [ObservableProperty] private bool _hasMoreFaces;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NotThisPersonCommand), nameof(AssignSelectedCommand), nameof(UseAsCoverCommand))] private int _selectedFaces;

    public bool HasPerson => Selected is not null && !ShowUnassigned;
    public bool ShowPhotos => HasPerson && !ShowFaces;
    public bool ShowFaceTiles => ShowUnassigned || (HasPerson && ShowFaces);
    public string NameButton => Selected?.Summary.Name is null ? "Name" : "Rename";
    public string DeleteButton => Selected?.Summary.Name is null ? "Dismiss" : "Delete";
    public string HideButton => Selected?.Summary.Hidden == true ? "Show in People" : "Hide";
    public string PersonTitle => ShowUnassigned ? "Faces nobody is assigned to" : Selected?.Name ?? "";

    partial void OnSelectedChanged(PersonEntryViewModel? value)
    {
        if (_refreshing) return;
        if (value is null) { Grid.Clear(); return; }
        ShowUnassigned = false;
        NameText = value.Summary.Name ?? "";
        Message = null;
        _ = ShowPersonAsync(value, keepPosition: false);
    }

    partial void OnShowFacesChanged(bool value) { if (Selected is { } p && !ShowUnassigned) _ = ShowPersonAsync(p, keepPosition: true); }
    partial void OnShowHiddenChanged(bool value) => _ = RefreshAsync();

    private async Task ShowPersonAsync(PersonEntryViewModel person, bool keepPosition)
    {
        if (ShowFaces) { await LoadFacesAsync(reset: true); return; }
        Grid.Filter.PersonIds.Clear();
        Grid.Filter.PersonIds.Add(person.Summary.Id);
        await Grid.ReloadAsync(keepPosition);
    }

    /// <summary>Reads the people again, keeping the selection.</summary>
    public async Task RefreshAsync()
    {
        var hidden = ShowHidden;
        var (people, unassigned) = await Task.Run(() => (_library.People.People(forViewers: false, includeHidden: hidden), _library.People.Unassigned(0, 1).Total));
        UnassignedLabel = $"Faces nobody is assigned to ({unassigned:N0})";
        var keep = Selected?.Summary.Id;
        _refreshing = true;
        try
        {
            foreach (var p in People) p.Release();
            People.Clear();
            // Named people first (most photos first), then the suggestions.
            foreach (var p in people.Where(p => p.Name is not null).Concat(people.Where(p => p.Name is null)))
                People.Add(new PersonEntryViewModel(_library, p));
            IsEmpty = People.Count == 0;
            Selected = People.FirstOrDefault(p => p.Summary.Id == keep) ?? (ShowUnassigned ? null : People.FirstOrDefault());
        }
        finally { _refreshing = false; }
        OnPropertyChanged(nameof(NameButton));
        OnPropertyChanged(nameof(DeleteButton));
        OnPropertyChanged(nameof(HideButton));
        OnPropertyChanged(nameof(PersonTitle));
        if (ShowUnassigned) return;
        if (Selected is null) { Grid.Clear(); return; }
        NameText = Selected.Summary.Name ?? "";
        await ShowPersonAsync(Selected, keepPosition: Selected.Summary.Id == keep);
    }

    /// <summary>Selects a person (from the viewer's list of people in a photo), showing their photos.</summary>
    public async Task SelectPersonAsync(long id)
    {
        ShowUnassigned = false;
        ShowFaces = false;
        if (People.All(p => p.Summary.Id != id)) await RefreshAsync();
        Selected = People.FirstOrDefault(p => p.Summary.Id == id) ?? Selected;
    }

    // ---------------------------------------------------------------- faces

    private async Task LoadFacesAsync(bool reset)
    {
        var offset = reset ? 0 : Faces.Count;
        var person = Selected?.Summary.Id;
        var unassigned = ShowUnassigned;
        var (tiles, total) = await Task.Run(() =>
        {
            if (unassigned)
            {
                var (list, n) = _library.People.Unassigned(offset, FacePage);
                return (list.Select(s => (s.Face, s.PersonId, s.PersonName, s.Similarity)).ToList(), n);
            }
            if (person is not { } id) return (new List<(FaceInfo, long?, string?, float?)>(), 0L);
            var (faces, t) = _library.People.FacesOf(id, offset, FacePage);
            return (faces.Select(f => (f, (long?)null, (string?)null, (float?)null)).ToList(), t);
        });
        if (reset) { foreach (var f in Faces) f.Release(); Faces.Clear(); }
        foreach (var (face, suggested, name, similarity) in tiles)
        {
            var tile = new FaceTileViewModel(_library, face, suggested, name, similarity, this);
            Faces.Add(tile);
        }
        FaceTotal = total;
        HasMoreFaces = Faces.Count < total;
        SelectedFaces = Faces.Count(f => f.IsSelected);
    }

    internal void OnFaceSelectionChanged() => SelectedFaces = Faces.Count(f => f.IsSelected);

    private List<long> SelectedFaceIds() => Faces.Where(f => f.IsSelected).Select(f => f.Face.Id).ToList();

    private void RemoveTiles(IReadOnlyCollection<long> ids)
    {
        foreach (var t in Faces.Where(f => ids.Contains(f.Face.Id)).ToList()) { t.Release(); Faces.Remove(t); }
        FaceTotal = Math.Max(0, FaceTotal - ids.Count);
        SelectedFaces = Faces.Count(f => f.IsSelected);
    }

    [RelayCommand]
    private async Task ShowUnassignedFacesAsync()
    {
        ShowUnassigned = true;
        _refreshing = true;
        try { Selected = null; } finally { _refreshing = false; }
        Message = null;
        await LoadFacesAsync(reset: true);
    }

    [RelayCommand]
    private Task MoreFacesAsync() => LoadFacesAsync(reset: false);

    private bool HasSelectedFaces => SelectedFaces > 0;
    private bool OneSelectedFace => SelectedFaces == 1 && HasPerson;

    [RelayCommand(CanExecute = nameof(HasSelectedFaces))]
    private async Task NotThisPersonAsync()
    {
        var ids = SelectedFaceIds();
        await Task.Run(() => _library.People.Unassign(ids));
        Message = $"{Format.Count(ids.Count, "face", "faces")} removed; they won't be put back.";
        RemoveTiles(ids);
        await RefreshListOnlyAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedFaces))]
    private async Task AssignSelectedAsync()
    {
        var ids = SelectedFaceIds();
        var name = AssignText.Trim();
        if (name.Length == 0) { Message = "Type who it is first."; return; }
        await Task.Run(() => _library.People.Assign(ids, null, name));
        Message = $"{Format.Count(ids.Count, "face", "faces")} assigned to {name}.";
        AssignText = "";
        RemoveTiles(ids);
        await RefreshListOnlyAsync();
    }

    [RelayCommand(CanExecute = nameof(OneSelectedFace))]
    private async Task UseAsCoverAsync()
    {
        if (Selected is not { } p || SelectedFaceIds() is not [var face]) return;
        await Task.Run(() => _library.People.SetCover(p.Summary.Id, face));
        Message = "Cover chosen.";
        await RefreshListOnlyAsync();
    }

    /// <summary>Agrees with a face's suggestion: assigns it to the person it looks like.</summary>
    internal async Task AcceptSuggestionAsync(FaceTileViewModel tile)
    {
        if (tile.SuggestedPersonId is not { } person) return;
        await Task.Run(() => _library.People.Assign([tile.Face.Id], person, null));
        RemoveTiles([tile.Face.Id]);
        await RefreshListOnlyAsync();
    }

    /// <summary>Updates the list's names, counts and covers, without reloading the photos or faces on the right.</summary>
    private async Task RefreshListOnlyAsync()
    {
        var keepFaces = ShowFaceTiles;
        var unassigned = ShowUnassigned;
        _refreshing = true;
        try
        {
            var hidden = ShowHidden;
            var keep = Selected?.Summary.Id;
            var (people, count) = await Task.Run(() => (_library.People.People(false, hidden), _library.People.Unassigned(0, 1).Total));
            UnassignedLabel = $"Faces nobody is assigned to ({count:N0})";
            foreach (var p in People) p.Release();
            People.Clear();
            foreach (var p in people.Where(p => p.Name is not null).Concat(people.Where(p => p.Name is null))) People.Add(new PersonEntryViewModel(_library, p));
            IsEmpty = People.Count == 0;
            Selected = unassigned ? null : People.FirstOrDefault(p => p.Summary.Id == keep);
        }
        finally { _refreshing = false; }
        OnPropertyChanged(nameof(PersonTitle));
        if (!keepFaces && Selected is { } s) await ShowPersonAsync(s, keepPosition: true);
    }

    // ---------------------------------------------------------------- the person

    [RelayCommand]
    private async Task SaveNameAsync()
    {
        if (Selected is not { } p) return;
        var name = NameText.Trim();
        if (name.Length == 0 || name == p.Summary.Name) return;
        try
        {
            await Task.Run(() => _library.People.Rename(p.Summary.Id, name));
            Message = $"Named {name}.";
        }
        catch (PersonExistsException ex)
        {
            var shell = Shell;
            if (shell is null || await shell.ChooseAsync("Name taken", $"There is already a person called {name}. Add these faces to {name}?", ["Add to " + name, "Cancel"]) != 0) return;
            await Task.Run(() => _library.People.Merge(p.Summary.Id, ex.ExistingId));
            Message = $"Added to {name}.";
            await RefreshAsync();
            await SelectPersonAsync(ex.ExistingId);
            return;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (Selected is not { } p) return;
        var name = MergeText.Trim();
        var into = People.FirstOrDefault(x => string.Equals(x.Summary.Name, name, StringComparison.OrdinalIgnoreCase));
        if (into is null) { Message = $"Nobody is called {name}."; return; }
        if (into.Summary.Id == p.Summary.Id) { Message = "That is the same person."; return; }
        await Task.Run(() => _library.People.Merge(p.Summary.Id, into.Summary.Id));
        Message = $"Merged into {into.Summary.Name}.";
        MergeText = "";
        await RefreshAsync();
        await SelectPersonAsync(into.Summary.Id);
    }

    [RelayCommand]
    private async Task ToggleHiddenAsync()
    {
        if (Selected is not { } p) return;
        await Task.Run(() => _library.People.SetHidden(p.Summary.Id, !p.Summary.Hidden));
        Message = p.Summary.Hidden ? "Shown in People again." : "Hidden from the People view (for viewers of the web gallery too).";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } p) return;
        var what = p.Summary.Name is null ? "Dismiss this suggestion? Its faces stay unnamed." : $"Delete {p.Summary.Name}? Their faces stay, unnamed; photos are not touched.";
        if (Shell is { } shell && await shell.ChooseAsync(p.Summary.Name is null ? "Dismiss" : "Delete person", what, [p.Summary.Name is null ? "Dismiss" : "Delete", "Cancel"]) != 0) return;
        await Task.Run(() => _library.People.Delete(p.Summary.Id));
        Message = p.Summary.Name is null ? "Dismissed: those faces stay unnamed." : $"Deleted {p.Summary.Name}.";
        _refreshing = true;
        try { Selected = null; } finally { _refreshing = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task GroupNowAsync()
    {
        Message = "Grouping faces…";
        var pass = await Task.Run(() => _library.People.RunPass());
        Message = $"{pass.Matched:N0} faces joined a person; {pass.Grouped:N0} formed {Format.Count(pass.NewPeople, "new suggestion", "new suggestions")}.";
        await RefreshAsync();
    }

}

/// <summary>A person in the list, with their cover face loaded while the entry is on screen.</summary>
public sealed partial class PersonEntryViewModel : ObservableObject, IRealizable
{
    private const int CoverSize = 96;
    private readonly LibraryHost _library;
    private bool _realized;

    public PersonEntryViewModel(LibraryHost library, PersonSummary summary)
    {
        _library = library;
        Summary = summary;
    }

    public PersonSummary Summary { get; }
    public string Name => Summary.Name ?? "Unnamed";
    public bool IsSuggestion => Summary.Name is null;
    public string Detail => Summary.Name is null
        ? $"Suggestion · {Format.Count((int)Summary.FaceCount, "face", "faces")} in {Format.Count((int)Summary.ItemCount, "photo", "photos")}"
        : Format.Count((int)Summary.ItemCount, "photo", "photos") + (Summary.Hidden ? " · hidden" : "");

    [ObservableProperty] private Bitmap? _cover;

    public async void Realize()
    {
        if (_realized) return;
        _realized = true;
        if (Summary.CoverFaceId is not { } face) return;
        var bitmap = await FaceBitmaps.LoadAsync(_library, face, CoverSize);
        if (!_realized) { bitmap?.Dispose(); return; }
        Cover = bitmap;
    }

    public void Release()
    {
        _realized = false;
        var old = Cover;
        Cover = null;
        if (old is not null) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
    }
}

/// <summary>A face picture in the faces view: selectable, with the person it looks like when nobody is assigned.</summary>
public sealed partial class FaceTileViewModel : ObservableObject, IRealizable
{
    private const int Size = 160;
    private readonly LibraryHost _library;
    private readonly PeopleViewModel _owner;
    private bool _realized;

    public FaceTileViewModel(LibraryHost library, FaceInfo face, long? suggestedPersonId, string? suggestedName, float? similarity, PeopleViewModel owner)
    {
        _library = library;
        _owner = owner;
        Face = face;
        SuggestedPersonId = suggestedPersonId;
        Suggestion = suggestedPersonId is null ? null : $"{suggestedName ?? "Unnamed group"}?";
        SuggestionTip = suggestedPersonId is null ? null : $"Looks like {suggestedName ?? "an unnamed group"} (similarity {similarity:0.00}): click to agree";
    }

    public FaceInfo Face { get; }
    public long? SuggestedPersonId { get; }
    public string? Suggestion { get; }
    public string? SuggestionTip { get; }
    public bool HasSuggestion => Suggestion is not null;

    [ObservableProperty] private Bitmap? _picture;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.OnFaceSelectionChanged();

    [RelayCommand]
    private Task AcceptAsync() => _owner.AcceptSuggestionAsync(this);

    public async void Realize()
    {
        if (_realized) return;
        _realized = true;
        var bitmap = await FaceBitmaps.LoadAsync(_library, Face.Id, Size);
        if (!_realized) { bitmap?.Dispose(); return; }
        Picture = bitmap;
    }

    public void Release()
    {
        _realized = false;
        var old = Picture;
        Picture = null;
        if (old is not null) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
    }
}

/// <summary>Face pictures from the library's cache (made when first asked for), as bitmaps.</summary>
internal static class FaceBitmaps
{
    public static async Task<Bitmap?> LoadAsync(LibraryHost library, long faceId, int size)
    {
        try
        {
            var path = await library.FaceCrops.GetAsync(faceId, size);
            return path is null ? null : await Task.Run(() => { using var s = File.OpenRead(path); return new Bitmap(s); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }
}
