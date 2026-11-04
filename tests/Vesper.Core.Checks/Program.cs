using Vesper.Core;
using Vesper.App;

if (args.Contains("--search-benchmark", StringComparer.Ordinal))
{
    SearchChecks.Benchmark();
    return;
}

string root = Path.Combine(Path.GetTempPath(), "VesperChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int count = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS: " + description); count++;
}

try
{
    string first = Path.Combine(root, "disc-one");
    string second = Path.Combine(root, "disc-two");
    Directory.CreateDirectory(first); Directory.CreateDirectory(second);
    string a = Path.Combine(first, "测试歌曲.mp4");
    string b = Path.Combine(second, "同内容副本.mp4");
    await File.WriteAllTextAsync(a, "same media bytes");
    File.Copy(a, b);
    string other = Path.Combine(first, "下一首.mp4");
    await File.WriteAllTextAsync(other, "different media bytes");
    await File.WriteAllTextAsync(Path.Combine(first, "说明.txt"), "ignore");
    ScanResult scan = await LibraryScanner.ScanAsync([first, second, first], []);
    Check(scan.Songs.Count == 2 && scan.Songs.Single(s => s.Title == "测试歌曲").Sources.Count == 2,
        "相同内容跨目录合并，重复目录不重复收录，跳过非媒体文件");
    Song song = scan.Songs.Single(s => s.Title == "测试歌曲");
    Song next = scan.Songs.Single(s => s.Title == "下一首");
    File.Delete(a);
    Check(song.IsAvailable && song.AvailablePath == b, "一个来源丢失后使用另一有效来源");
    var state = new LibraryState { Directories = [first, second], Songs = scan.Songs, Favorites = [song.Id] };
    var store = new LibraryStore(Path.Combine(root, "data", "library.json"));
    await Task.WhenAll(store.SaveAsync(state), store.SaveAsync(state));
    LibraryState restored = await store.LoadAsync();
    Check(restored.Favorites.Contains(song.Id) && restored.Songs.Count == 2,
        "曲库与收藏持久化，并发保存不会破坏记录");
    ScanResult removedDirectory = await LibraryScanner.ScanAsync([first], scan.Songs);
    Check(!removedDirectory.Songs.Single(s => s.Id == song.Id).IsAvailable && File.Exists(b),
        "移除最后有效目录后保留不可用记录，不删除原文件");
    ScanResult recovered = await LibraryScanner.ScanAsync([first, second], removedDirectory.Songs);
    Check(recovered.Songs.Single(s => s.Id == song.Id).IsAvailable, "重新添加目录恢复原歌曲 ID");
    var queue = new PlaybackQueue();
    QueueEntry original = queue.Add(song); queue.Add(next); queue.Add(song);
    Check(queue.Pending.Count == 3 && queue.Pending[0].Id != queue.Pending[2].Id,
        "同曲重复点歌具有独立队列条目");
    Check(queue.StartNext(recovered.Songs, out _)?.Id == song.Id && queue.Pending.Count == 2,
        "开始演唱只取出队首，当前歌曲与待播分开");
    queue.MarkPlaying(); queue.MarkPaused();
    Check(queue.State == PlaybackState.Paused && queue.Current?.Id == original.Id,
        "暂停保持当前歌曲与待播顺序");
    queue.ExitSession();
    Check(queue.Current is null && queue.Pending[0].Id == original.Id && queue.Pending.Count == 3,
        "退出本场将当前歌曲放回队首，保留原顺序");
    File.Delete(b);
    Check(queue.StartNext(recovered.Songs, out int skipped)?.Id == next.Id && skipped == 1,
        "轮到已失效歌曲时跳过，播放下一有效歌曲");
    Check(queue.StartNext(recovered.Songs, out skipped) is null && skipped == 1
        && queue.State == PlaybackState.Idle && queue.Pending.Count == 0,
        "全部剩余歌曲失效时停止推进，进入等待状态");
    queue.Add(next);
    Check(queue.State == PlaybackState.Idle && queue.Current is null,
        "队列从空变为非空不会自动开唱");
    QueueChecks.Run(other, Check);
    await QueueChecks.RunPersistenceAsync(root, Check);
    await File.WriteAllTextAsync(Path.Combine(root, "data", "library.json"), "{ broken json");
    bool rejected = false;
    try { await store.LoadAsync(); } catch (System.Text.Json.JsonException) { rejected = true; }
    Check(rejected, "损坏记录明确报错，不静默覆盖为空曲库");
    string uiStore = Path.Combine(root, "ui", "library.json");
    var model = new MainViewModel(uiStore);
    await model.InitializeAsync();
    Check(model.ShowHomeHint && model.CanManage && model.Results.Count == 0,
        "空曲库提示前往曲库页，初始化完成后允许导入");
    await model.AddDirectoryAsync(first);
    await model.AddDirectoryAsync(first + Path.DirectorySeparatorChar);
    Check(model.Directories.Count == 1 && model.Results.Count == 1 && !model.ShowHomeHint,
        "添加目录显示扫描结果，路径末尾分隔符不会重复收录目录");
    LibrarySongItem imported = model.Results.Single();
    await model.ToggleFavoriteAsync(imported);
    await model.RemoveDirectoryAsync(model.Directories.Single().Path);
    Check(File.Exists(other) && model.Results.Count == 0 && model.ShowHomeHint && model.Library.Favorites.Contains(imported.Song.Id),
        "移除目录保留硬盘歌曲与收藏，默认隐藏失效记录");
    model.HideUnavailable = false;
    Check(model.Results.Count == 1 && !model.Results[0].IsAvailable && model.Results[0].IsFavorite,
        "关闭隐藏选项可查看灰显的失效收藏歌曲");
    await model.AddDirectoryAsync(first);
    var reopened = new MainViewModel(uiStore);
    await reopened.InitializeAsync();
    Check(reopened.Results.Count == 1 && reopened.Results[0].IsFavorite && reopened.Directories.Count == 1,
        "重启恢复目录、歌曲与收藏，重新收录的文件恢复可用");
    await File.WriteAllTextAsync(uiStore, "{\"Directories\":null}");
    string originalRecord = await File.ReadAllTextAsync(uiStore);
    var broken = new MainViewModel(uiStore);
    await broken.InitializeAsync();
    await broken.AddDirectoryAsync(first);
    Check(broken.ReadOnly && broken.HasProblem && !broken.CanManage
        && await File.ReadAllTextAsync(uiStore) == originalRecord,
        "结构损坏的曲库进入只读状态，拒绝导入且保留原记录");
    var legacyPreferences = System.Text.Json.JsonSerializer.Deserialize<ShellPreferences>("{\"PaneOpen\":false,\"SystemAccent\":true}");
    Check(legacyPreferences is { PaneOpen: false, SystemAccent: true, HideUnavailable: true },
        "旧偏好记录保留主题和导航选择，新增隐藏不可用选项默认开启");
    string preferenceJson = System.Text.Json.JsonSerializer.Serialize(new ShellPreferences(false, true, false));
    Check(System.Text.Json.JsonSerializer.Deserialize<ShellPreferences>(preferenceJson)?.HideUnavailable == false,
        "设置中的隐藏不可用选项可保存并恢复为关闭状态");
    await LibraryScanChecks.RunAsync(root, Check);
    await FavoriteChecks.RunAsync(root, Check);
    await SongDetailsChecks.RunAsync(root, Check);
    ListCoverChecks.Run(Check);
    HeroPaletteChecks.Run(Check);
    await CatalogChecks.RunAsync(root, Check);
    AlbumTrackLayoutChecks.Run(Check);
    await BackgroundVideoChecks.RunAsync(root, Check);
    await BackgroundVideoQueueChecks.RunAsync(root, Check);
    await AlbumArtworkChecks.RunAsync(root, Check);
    await LibraryEnrichmentChecks.RunAsync(Check);
    await EnrichmentModelChecks.RunAsync(root, Check);
    await SongAssetStateChecks.RunAsync(root, Check);
    await AutomaticLookupChecks.RunAsync(root, Check);
    await OnlineArtistProfileChecks.RunAsync(Check);
    await ArtworkDiskCacheChecks.RunAsync(root, Check);
    await AdditionalSourceChecks.RunAsync(Check);
    await AdditionalSourceCancellationChecks.RunAsync(Check);
    await AdditionalSourceCacheBoundChecks.RunAsync(Check);
    LyricsDisplayChecks.Run(Check);
    await OverviewChecks.RunAsync(root, Check);
    MatroskaChecks.Run(Check);
    SearchChecks.Run(Check);
    await SearchChecks.RunRequestGateAsync(Check);
    await SearchChecks.RunModelAsync(root, Check);
    model.BeginMetadataRead();
    Check(model.HasBackgroundActivity && !model.Idle && model.CanManage && model.ActivityLabel == "读取歌曲资料",
        "画质与艺人资料读取也纳入后台仪表，并保持目录操作可用");
    model.EndMetadataRead();
    Check(model.Idle && !model.HasBackgroundActivity, "资料读取完成后仪表恢复就绪状态");
    var activity = new ActivityStatusPresenter();
    var scanning = new ActivitySnapshot(true, "正在扫描", "扫描进度", "\uE73E");
    var metadata = new ActivitySnapshot(true, "读取资料", "资料进度", "\uE73E");
    var ready = new ActivitySnapshot(false, "就绪", "任务已完成", "\uE73E");
    Check(activity.Request(scanning, TimeSpan.Zero) && activity.IsBusy, "首个后台状态可立即显示");
    activity.Request(metadata, TimeSpan.FromMilliseconds(100));
    activity.Request(ready, TimeSpan.FromMilliseconds(200));
    Check(!activity.Flush(TimeSpan.FromMilliseconds(999)) && activity.Label == "正在扫描" && activity.IsBusy,
        "不足一秒时文字与忙碌图形均保持原状态，不随实时报告闪动");
    Check(activity.Flush(TimeSpan.FromSeconds(1)) && activity.Label == "就绪" && activity.Idle,
        "一秒后合并到最新状态，跳过已经过期的中间任务");
    activity.Request(scanning, TimeSpan.FromMilliseconds(1001));
    Check(!activity.Flush(TimeSpan.FromMilliseconds(1999)) && activity.Idle
        && activity.Flush(TimeSpan.FromSeconds(2)) && activity.IsBusy,
        "包括就绪在内的每个已展示状态均至少保留一秒");
    Console.WriteLine($"{count} checks passed.");
}
finally
{
    // 仅清理本测试通过 Guid 创建的临时目录。
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(root).StartsWith("VesperChecks-", StringComparison.Ordinal))
        Directory.Delete(root, recursive: true);
}
