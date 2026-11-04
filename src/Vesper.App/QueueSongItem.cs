using System.ComponentModel;
using Vesper.Core;

namespace Vesper.App;

public sealed class QueueSongItem : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public QueueEntry Entry { get; private set; }
    public LibrarySongItem? Song { get; private set; }
    private string[] sources = [];
    private bool hovered, focused;
    public int Position { get; private set; }
    public bool IsPending => Position > 0;
    public string Number => IsPending ? Position.ToString() : "♪";
    public string Title => Song?.Title ?? Entry.Title;
    public string Artist => Song?.Artist ?? "未知艺人";
    public object? Cover => Song?.ListCover;
    public bool HasCover => Cover is not null;
    public bool HasNoCover => !HasCover;
    public bool IsAvailable { get; private set; }
    public bool IsUnavailable => !IsAvailable;
    public double RowOpacity => IsAvailable ? 1 : .5;
    public bool ActionsVisible { get; private set; }
    public double ActionsOpacity => ActionsVisible ? 1 : 0;
    public QueueSongItem(QueueEntry entry) { Entry = entry; }
    public void Update(QueueEntry entry, LibrarySongItem? song, int position)
    {
        bool sourceChanged = !sources.SequenceEqual(song?.Song.Sources ?? [], StringComparer.OrdinalIgnoreCase);
        if (!ReferenceEquals(Song, song))
        {
            if (Song is not null) Song.PropertyChanged -= OnSongChanged;
            Song = song;
            if (Song is not null) Song.PropertyChanged += OnSongChanged;
            sourceChanged = true;
        }
        Entry = entry; Position = position;
        sources = song?.Song.Sources.ToArray() ?? [];
        if (sourceChanged) IsAvailable = song?.IsAvailable ?? false;
        Notify();
    }
    private void OnSongChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LibrarySongItem.IsAvailable)) IsAvailable = Song?.IsAvailable ?? false;
        Notify();
    }
    public void SetAvailable(bool value) { if (IsAvailable == value) return; IsAvailable = value; Notify(); }
    public void ShowActions(bool value) { hovered = value; UpdateActions(); }
    public void SetFocused(bool value) { focused = value; UpdateActions(); }
    private void UpdateActions()
    {
        bool visible = IsPending && (hovered || focused);
        if (ActionsVisible == visible) return;
        ActionsVisible = visible; Notify(nameof(ActionsVisible)); Notify(nameof(ActionsOpacity));
    }
    public void SetPosition(int position)
    { if (Position == position) return; Position = position; Notify(nameof(Position)); Notify(nameof(Number)); }
    private void Notify(string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public void Dispose() { if (Song is not null) Song.PropertyChanged -= OnSongChanged; }
}
