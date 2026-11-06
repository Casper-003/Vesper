using Vesper.Core;

internal static class HomeRecommendationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var songs = Enumerable.Range(0, 30).Select(i => new Song($"home-{i}", $"Song {i}", [])).ToArray();
        var session = new HomeRecommendations(12, new Random(7));
        check(session.Select([]).Count == 0, "空曲库不提前冻结首页推荐，等待首次导入");
        var first = session.Select(songs).ToArray();
        check(first.Length == 12 && first.Distinct().Count() == 12 && first.All(id => songs.Any(song => song.Id == id)),
            "首页推荐只从传入的可用本地歌曲中选取不重复的一组");
        check(session.Select(songs.Reverse()).SequenceEqual(first), "返回或曲库排序变化不重抽首页推荐");
        check(session.Select([]).SequenceEqual(first) && session.Select([new("later", "Later", [])]).SequenceEqual(first),
            "推荐全部不可用或后来导入其他歌曲都不补抽、不改变原组顺序");
        var copy = (string[])session.Select(songs); copy[0] = "changed";
        check(session.Select(songs).SequenceEqual(first), "外部修改返回数组不能污染本次运行的推荐快照");
        check(new HomeRecommendations(12, new Random(8)).Select(songs.Take(2)).Count == 2,
            "可用歌曲不足一组时显示实际数量，不复制或凑空卡片");
    }
}
