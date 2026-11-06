using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Vesper.Core;

namespace Vesper.App;

public sealed class HomeCard : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public bool IsArtist { get; }
    public double Size => IsArtist ? 224 : 160;
    public string Glyph => IsArtist ? "\uE77B" : "\uE93C";
    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public string Availability { get; private set; } = "";
    public double CardOpacity { get; private set; } = 1;
    public ImageSource? Image { get; private set; }
    public bool HasImage => Image is not null;
    public bool NoImage => Image is null;
    public Stretch ImageStretch { get; private set; } = Stretch.UniformToFill;
    internal LibrarySongItem? Song { get; private set; }
    internal CatalogArtist? Artist { get; private set; }
    internal long ArtistImageRevision { get; set; } = -1;
    internal string SongImageIdentity { get; set; } = "";
    internal HomeCard(string id, bool artist) { Id = id; IsArtist = artist; }
    internal void Update(LibrarySongItem song)
    {
        if (SongImageIdentity.Length > 0 && SongImageIdentity != song.ArtworkIdentity) SetImage(null);
        Song = song;
        SetText(song.Title, song.Artist, song.IsAvailable ? "" : "文件不可用", song.IsAvailable ? 1 : .45);
    }
    internal void Update(CatalogArtist artist)
    {
        if (Artist is not null && !Artist.ExternalIds.SequenceEqual(artist.ExternalIds)) SetImage(null);
        Artist = artist; SetText(artist.Name, "", "", 1);
    }
    private void SetText(string title, string subtitle, string availability, double opacity)
    {
        if (Title != title) { Title = title; Notify(nameof(Title)); }
        if (Subtitle != subtitle) { Subtitle = subtitle; Notify(nameof(Subtitle)); }
        if (Availability != availability) { Availability = availability; Notify(nameof(Availability)); }
        if (CardOpacity != opacity) { CardOpacity = opacity; Notify(nameof(CardOpacity)); }
    }
    internal void SetImage(ImageSource? image, bool logo = false)
    {
        if (image is null) { ArtistImageRevision = -1; SongImageIdentity = ""; }
        var stretch = logo ? Stretch.Uniform : Stretch.UniformToFill;
        if (ImageStretch != stretch) { ImageStretch = stretch; Notify(nameof(ImageStretch)); }
        if (ReferenceEquals(Image, image)) return;
        Image = image; Notify(nameof(Image)); Notify(nameof(HasImage)); Notify(nameof(NoImage));
    }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
    public override string ToString() => Title;
}
