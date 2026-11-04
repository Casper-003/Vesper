using Vesper.Core;

internal static class AlbumTrackLayoutChecks
{
    public static void Run(Action<bool, string> check)
    {
        var single = AlbumTrackLayout.Build([new(1, 2), new(1, 7), new(1, null)]);
        check(single.Select(label => label.Number).SequenceEqual(["2", "7", "—"])
            && single.All(label => label.DiscHeading.Length == 0),
            "专辑首列保留真实2和7曲号，未知曲号显示破折号，单盘不按本地行号补造位置");
        var multiple = AlbumTrackLayout.Build([new(1, 2), new(1, 7), new(2, 1), new(2, 5)]);
        check(multiple.Select(label => label.DiscHeading).SequenceEqual(["Disc 1", "", "Disc 2", ""])
            && multiple.Select(label => label.Number).SequenceEqual(["2", "7", "1", "5"]),
            "多盘按各盘首个已收录曲目标注Disc1和Disc2，各盘曲号独立保留");
        var onlySecondDisc = AlbumTrackLayout.Build([new(2, 4), new(2, null)]);
        check(onlySecondDisc[0].DiscHeading == "Disc 2" && onlySecondDisc[1].DiscHeading == ""
            && onlySecondDisc[0].Number == "4" && onlySecondDisc[1].Number == "—",
            "本地仅收录第二盘也准确标明Disc2，不伪造第一盘或缺曲");
        var unknownDisc = AlbumTrackLayout.Build([new(1, 1), new(2, 1), new(null, 3), new(null, null)]);
        check(unknownDisc.Select(label => label.DiscHeading).SequenceEqual(["Disc 1", "Disc 2", "未标注盘号", ""])
            && unknownDisc[^1].Number == "—",
            "多盘中的未知盘号单独标注，不默认为Disc1；未知曲号不使用分组行号");
        var invalid = AlbumTrackLayout.Build([new(0, -1), new(-2, 0), new(null, null)]);
        check(invalid.All(label => label.Number == "—" && label.DiscHeading.Length == 0)
            && AlbumTrackLayout.Build([]).Count == 0,
            "无效盘号曲号及空专辑保持未知和空状态，不生成虚构序号或盘标题");
        var catalog = new LibraryCatalog { Tracks = [
            new("d2", "song2", "album", 2, 1), new("unknown-disc", "song4", "album", null, 1),
            new("d1-unknown", "song3", "album", 1, null), new("d1", "song1", "album", 1, 2)] };
        check(catalog.GetAlbumTracks("album").Select(track => track.Id).SequenceEqual(["d1", "d1-unknown", "d2", "unknown-disc"]),
            "每张盘的未知曲号仍归本盘末尾，下一盘不会提前插入，未知盘号独立放末尾");
    }
}
