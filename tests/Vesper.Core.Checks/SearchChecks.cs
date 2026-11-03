using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vesper.App;
using Vesper.Core;

internal static class SearchChecks
{
    private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly SearchOptions All = new(200, 200, 200);

    private static SearchDocument Doc(string id, string title, string artist = "周杰伦", string album = "魔杰座") =>
        new(id, title, artist, album, true);

    private static SearchDocument[] Fixture() =>
    [
        Doc("jay-original", "稻香") with { GroupId = "g-jay", VersionId = "v-original", VersionName = "原版MV", IsOriginalMv = true, AddedAt = Epoch,
            Resources = [new("original-720", "jay-original", "synthetic/original.mkv", true, 720)] },
        Doc("jay-live", "稻香") with { GroupId = "g-jay", VersionId = "v-live", VersionName = "现场版", AddedAt = Epoch.AddDays(1),
            Resources = [new("live-1080", "jay-live", "synthetic/live.mkv", true, 1080), new("live-copy", "jay-live", "synthetic/copy.mkv", true, 1080, 1),
                new("live-4k", "jay-live", "synthetic/live4k.mkv", true, 2160)] },
        Doc("jay-accompaniment", "稻香") with { GroupId = "g-jay", VersionId = "v-accompaniment", VersionName = "伴奏版", AddedAt = Epoch.AddDays(2) },
        Doc("cover", "稻香", "另一位歌手"),
        Doc("other-jay", "晴天", album: "叶惠美"),
        Doc("prefix", "稻香路", "林间"),
        Doc("contains", "故乡稻香", "远方"),
        Doc("literal-dx", "DX", "Latin Artist", "Letters"),
        Doc("initials-concat", "浙江列岛线", "无关艺人", "缩写集"),
        Doc("literal-d", "D", "A", "Letters"),
        Doc("literal-live", "Live", "Latin Artist", "Letters"),
        Doc("cplusplus", "C++", "A-Lin", "Symbols"),
        Doc("letter-c", "C", "Alin", "Symbols"),
        Doc("polyphone", "重庆", "重庆乐队", "城市"),
        Doc("album-a", "回声甲", "甲艺人", "回声") with { AlbumArtist = "甲艺人" },
        Doc("album-b", "回声乙", "乙艺人", "回声") with { AlbumArtist = "乙艺人" },
        Doc("unknown-live-a", "夜航", "夜航乐队", "") with { VersionName = "现场版" },
        Doc("unknown-live-b", "夜航", "夜航乐队", "") with { VersionName = "现场版" },
        Doc("cooperative", "合唱之歌", "甲艺人 / 乙艺人", "合唱集") with { Artists = [new("甲艺人", "artist-a"), new("乙艺人", "artist-b")] }
    ];

    public static void Run(Action<bool, string> check)
    {
        SearchDocument[] documents = Fixture();
        LocalSearchIndex index = LocalSearchIndex.Build(documents, 7, true);
        SearchResponse Search(string query) => index.Search(query, 11, All);
        foreach (string query in new[] { "稻香", "daoxiang", "DX", "ｄｘ" })
            check(Search(query).Songs.Items.Any(hit => hit.GroupId == "g-jay"), $"搜索 {query} 可召回同一目标歌曲组");
        var traditional = Search("周杰倫");
        check(traditional.Artists.Items.Any(hit => hit.Name == "周杰伦") && traditional.Songs.Items.Any(hit => hit.GroupId == "g-jay"),
            "繁体艺人输入同时匹配正确艺人及其歌曲");
        foreach (string query in new[] { "稻香 周杰伦", "周杰伦 稻香", "zjl dx", "周杰伦 daoxiang" })
        {
            var result = Search(query);
            check(result.Songs.Items.Select(hit => hit.GroupId).SequenceEqual(["g-jay"]), $"组合查询 {query} 的全部词项只覆盖同一候选版本");
            check(result.Artists.Items.Count == 0 && result.Albums.Items.Count == 0, $"组合查询 {query} 不从歌曲补造艺人或专辑卡片");
        }
        var ranked = Search("稻香").Songs.Items.Select(hit => hit.GroupId).ToArray();
        check(Array.IndexOf(ranked, "prefix") > Array.IndexOf(ranked, "g-jay") && Array.IndexOf(ranked, "contains") > Array.IndexOf(ranked, "prefix"),
            "真实标题精确匹配先于前缀，前缀先于连续包含");
        check(Search("DX").Songs.Items[0].GroupId == "literal-dx", "真实拉丁标题 DX 优先于拼音首字母匹配");
        check(Search("zjldx").Songs.Items.Any(hit => hit.GroupId == "initials-concat")
            && Search("zjl dx").Songs.Items.All(hit => hit.GroupId != "initials-concat"),
            "单词可按完整首字母匹配，多词不能先拼接缩写绕过每词证据要求");
        check(Search("d").Songs.Items.All(hit => hit.GroupId != "g-jay") && Search("d").Songs.Items.Any(hit => hit.GroupId == "literal-d"),
            "单拉丁字母仍查真实文本，但不启动中文全拼或首字母泛召回");
        check(Search("chongqing").Songs.Items.Any(hit => hit.GroupId == "polyphone") && Search("cq").Songs.Items.Any(hit => hit.GroupId == "polyphone"),
            "重庆多音词使用可验证的词组全拼与首字母");
        check(Search("C++").Songs.Items[0].GroupId == "cplusplus" && Search("A-Lin").Artists.Items[0].Name == "A-Lin",
            "含标点名称保留字面精确匹配优先级");
        check(Search("  周杰伦　 稻香  ").Songs.Items.Select(hit => hit.GroupId).SequenceEqual(["g-jay"]),
            "资料与查询统一 NFKC、首尾空白与连续空白处理");
        check(Search("Live").Songs.Items.Any(hit => hit.GroupId == "literal-live"), "单独搜索 Live 保留真实作品标题");
        check(Search("夜航").Songs.Items.Count == 2, "同歌名同艺人的两个未知现场文件不凭相似资料误合并");
        SearchDocument[] emptyIdentities =
        [Doc("unknown-empty-id", "空身份现场", "未知组合", "") with { GroupId = "", VersionId = "", VersionName = "现场版" },
         Doc("unknown-space-id", "空身份现场", "未知组合", "") with { GroupId = " \t ", VersionId = "　", VersionName = "现场版" }];
        var emptyIdentityHits = LocalSearchIndex.Build(emptyIdentities, 71, true).Search("空身份现场", 111).Songs.Items;
        check(emptyIdentityHits.Count == 2 && emptyIdentityHits.Select(hit => hit.GroupId).Distinct().Count() == 2
            && emptyIdentityHits.All(hit => !string.IsNullOrWhiteSpace(hit.GroupId) && hit.Versions.Count == 1
                && hit.Versions[0].VersionId == hit.Versions[0].SongId),
            "空或全空白的显式歌曲组与版本 ID 视为未知，回退各自单曲 ID，不能误合并");
        SearchDocument[] emptyEntities =
        [Doc("entity-empty-id", "空实体甲", "甲艺人", "空身份专辑") with { AlbumId = "", Artists = [new("甲艺人", "")] },
         Doc("entity-space-id", "空实体乙", "乙艺人", "空身份专辑") with { AlbumId = " \t ", Artists = [new("乙艺人", "　")] }];
        var emptyEntityIndex = LocalSearchIndex.Build(emptyEntities, 72, true);
        var emptyArtistHits = emptyEntityIndex.Search("艺人", 112).Artists.Items;
        var emptyAlbumHits = emptyEntityIndex.Search("空身份专辑", 113).Albums.Items;
        check(emptyArtistHits.Count == 2 && emptyArtistHits.Select(hit => hit.Id).Distinct().Count() == 2
            && emptyAlbumHits.Count == 2 && emptyAlbumHits.Select(hit => hit.Id).Distinct().Count() == 2
            && emptyArtistHits.All(hit => !string.IsNullOrWhiteSpace(hit.Id)) && emptyAlbumHits.All(hit => !string.IsNullOrWhiteSpace(hit.Id)),
            "空或全空白艺人及专辑 ID 使用保守稳定回退，不合并不同艺人及其同名专辑");
        var albums = Search("回声");
        check(albums.Albums.Items.Count == 2 && albums.Albums.Items.Select(hit => hit.Id).Distinct().Count() == 2
            && albums.Songs.Items.Any(hit => hit.GroupId == "album-a") && albums.Songs.Items.Any(hit => hit.GroupId == "album-b"),
            "同名不同艺人的两张专辑身份独立，专辑名同时召回本地歌曲");
        check(Search("乙艺人").Songs.Items.Any(hit => hit.GroupId == "cooperative") && Search("乙艺人").Artists.Items.Any(hit => hit.Id == "artist-b"),
            "合作署名的每个已知艺人均可独立查询");

        var group = Search("稻香").Songs.Items.Single(hit => hit.GroupId == "g-jay");
        check(group.RepresentativeVersionId == "v-original" && group.Versions.Count == 3, "原版 720p 仍作组代表，现场高画质不改代表项");
        var live = Search("稻香 现场版").Songs.Items.Single();
        var version = live.Versions.Single(hit => hit.VersionId == live.BestMatchedVersionId);
        check(live.RepresentativeVersionId == "v-original" && live.BestMatchedVersionId == "v-live"
            && live.MatchedVersionIds.SequenceEqual(["v-live"]) && live.MatchEvidence.HasVersionIntent,
            "版本限定保留组代表，并以明确的命中子版本 ID 与证据定位");
        check(version.PreferredResourceId == "live-4k" && version.SongId == "jay-live" && version.Resolution == 2160,
            "同一演唱版本的多来源与不同画质折叠，命中目标解析最高可用资源及正确曲库 ID");
        check(Search("稻香 live").Songs.Items.Single().BestMatchedVersionId == "v-live", "独立 live 限定词仅匹配有现场证据的版本");
        check(Search("稻香 现场版 伴奏版").Status == SearchStatus.NoMatch, "互斥版本要求不能从兄弟版本各取证据拼接命中");
        SearchDocument[] unavailable4k = documents.Select(doc => doc.SongId != "jay-live" ? doc : doc with
            { Resources = doc.Resources!.Select(resource => resource.Id == "live-4k" ? resource with { IsAvailable = false } : resource).ToArray() }).ToArray();
        var fallback = LocalSearchIndex.Build(unavailable4k, 8, true).Search("稻香 现场版", 12).Songs.Items.Single();
        check(fallback.Versions.Single(hit => hit.VersionId == "v-live").PreferredResourceId == "live-1080", "4K 失效时回退同版本次高画质和稳定来源顺序");
        var copyOnly = unavailable4k.Select(doc => doc.SongId != "jay-live" ? doc : doc with
            { Resources = doc.Resources!.Select(resource => resource.Id == "live-1080" ? resource with { IsAvailable = false } : resource).ToArray() });
        check(LocalSearchIndex.Build(copyOnly, 81, true).Search("稻香 现场版", 121).Songs.Items.Single().Versions.Single(hit => hit.VersionId == "v-live")
            .PreferredResourceId == "live-copy", "移除一个相同内容副本后，同版本其余已收录来源仍可使用");
        SearchDocument[] noOriginal = documents.Select(doc => doc.SongId == "jay-original" ? doc with { IsAvailable = false, Resources = [] } : doc).ToArray();
        check(LocalSearchIndex.Build(noOriginal, 9, true).Search("稻香", 13).Songs.Items.Single(hit => hit.GroupId == "g-jay").RepresentativeVersionId == "v-live",
            "无可用原版时选最早加入的可用演唱版本");
        var literalVersionTitle = LocalSearchIndex.Build(documents.Append(Doc("literal-version-title", "稻香 现场版", "真实标题艺人")), 10, true)
            .Search("稻香 现场版", 14).Songs.Items;
        check(literalVersionTitle[0].GroupId == "literal-version-title", "整句真实标题优先于推断出的版本解释");
        var titleAndArtist = LocalSearchIndex.Build(documents.Append(Doc("whole-query-title", "周杰伦 稻香", "标题艺人")), 12, true)
            .Search("周杰伦 稻香", 17).Songs.Items;
        check(titleAndArtist[0].GroupId == "g-jay", "通常歌名与艺人文本精确组合先于整句等于另一首真实标题");
        var sameVersionDocuments = LocalSearchIndex.Build(documents.Append(Doc("live-second-entry", "稻香") with
            { GroupId = "g-jay", VersionId = "v-live", VersionName = "现场版", AddedAt = Epoch.AddDays(1),
                Resources = [new("live-8k", "live-second-entry", "synthetic/live8k.mkv", true, 4320)] }), 13, true)
            .Search("稻香 现场版", 18).Songs.Items.Single();
        check(sameVersionDocuments.Versions.Count == 3 && sameVersionDocuments.Versions.Single(hit => hit.VersionId == "v-live").SongId == "live-second-entry",
            "有可靠版本身份的多个曲库条目按版本合并，资源选择返回实际对应的曲库条目 ID");
        SearchDocument[] splitEvidence =
        [Doc("split-a", "作品", "艺人", "甲专辑") with { GroupId = "split", VersionId = "split-a" },
         Doc("split-b", "作品", "艺人", "乙专辑") with { GroupId = "split", VersionId = "split-b" }];
        check(LocalSearchIndex.Build(splitEvidence, 11, true).Search("甲专辑 乙专辑", 15).Status == SearchStatus.NoMatch,
            "同组两个版本的专辑资料不能拼成一次组合匹配");
        check(Search(" 	　 ").Status == SearchStatus.Empty && Search(" 	　 ").Songs.Items.Count == 0, "空白输入为空查询，不列出全曲库");
        check(Search("不存在的目标xyz").Status == SearchStatus.NoMatch, "只有成功查询的三类均为空时才返回无匹配");
        check(Search("稻香").QueryId == 11 && Search("稻香").IndexRevision == 7, "结果携带查询与固定索引修订号");
        CheckPaging(check);
        bool cancelled = false;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { index.Search("稻香", 16, cancellationToken: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "核心查询遵守取消令牌");
    }

    private static void CheckPaging(Action<bool, string> check)
    {
        var documents = Enumerable.Range(0, 83).Select(i => Doc($"page-{i:D3}", $"共同标题 {i:D3}", $"共同艺人 {i:D3}", $"共同专辑 {i:D3}")).ToArray();
        var index = LocalSearchIndex.Build(documents, 20, true);
        var response = index.Search("共同", 30);
        var ids = new List<string>();
        var page = response.Songs;
        check(page.Items.Count == 30 && response.Artists.Items.Count == 5 && response.Albums.Items.Count == 5,
            "三类默认首批分别返回 30 歌曲组、5 艺人、5 专辑");
        do { ids.AddRange(page.Items.Select(hit => hit.GroupId)); if (!page.HasMore) break; page = response.GetSongsPage(page.ContinuationToken); } while (true);
        check(ids.Count == 83 && ids.Distinct().Count() == 83, "歌曲续页不重不漏，并在候选分组之后截取首屏");
        var again = index.Search("共同", 31, All);
        check(ids.SequenceEqual(again.Songs.Items.Select(hit => hit.GroupId)), "同查询同快照反复查询与续页具有稳定顺序");
        bool crossQueryRejected = false, crossCategoryRejected = false;
        try { again.GetSongsPage(response.Songs.ContinuationToken); } catch (ArgumentException) { crossQueryRejected = true; }
        try { response.GetArtistsPage(response.Songs.ContinuationToken); } catch (ArgumentException) { crossCategoryRejected = true; }
        check(crossQueryRejected && crossCategoryRejected, "续页标记绑定查询、快照与类别，拒绝跨查询或跨类别接页");
        int artists = 0, albums = 0;
        var artistPage = response.Artists;
        do { artists += artistPage.Items.Count; if (!artistPage.HasMore) break; artistPage = response.GetArtistsPage(artistPage.ContinuationToken); } while (true);
        var albumPage = response.Albums;
        do { albums += albumPage.Items.Count; if (!albumPage.HasMore) break; albumPage = response.GetAlbumsPage(albumPage.ContinuationToken); } while (true);
        check(artists == 83 && albums == 83, "艺人与专辑可各自独立完整续取");
    }

    public static async Task RunRequestGateAsync(Action<bool, string> check)
    {
        using var gate = new SearchRequestGate();
        var old = gate.BeginEdit();
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string visible = "";
        async Task CompleteLateAsync()
        {
            await oldRelease.Task;
            // Deliberately ignore cancellation: authority at the real write boundary is required.
            if (gate.CanPublish(old)) visible = "old";
        }
        var late = CompleteLateAsync();
        var current = gate.BeginEdit();
        if (gate.CanPublish(current)) visible = "current";
        oldRelease.SetResult(); await late;
        check(visible == "current" && old.CancellationToken.IsCancellationRequested, "人工延迟的旧查询即使忽略取消，也不能覆盖新查询结果");
        var cleared = gate.BeginEdit();
        visible = "";
        check(!gate.CanPublish(current) && gate.CanPublish(cleared), "清空先失效旧查询，迟到结果不能重新填回");
        gate.Leave();
        check(!gate.CanPublish(cleared), "离开页面后丢弃尚未上屏结果");
        var entered = gate.BeginEdit();
        check(entered.Session != cleared.Session && gate.CanPublish(entered) && !gate.CanPublish(old), "重新进入使用新搜索会话，旧页面回调失去发布权");
        gate.Dispose();
        check(!gate.CanPublish(entered), "关闭窗口后查询不再具有发布权");
    }

    public static async Task RunModelAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "search-model-media");
        Directory.CreateDirectory(folder);
        string media = Path.Combine(folder, "stored.mp4");
        await File.WriteAllTextAsync(media, "synthetic search media");
        string storePath = Path.Combine(root, "search-model.json");
        var initial = new LibraryState { Directories = [folder], Songs = [new("stored-id", "文件名回退", [media])],
            Details = new() { ["stored-id"] = new(Local: new("已存歌名", "旧艺人", "旧专辑")) } };
        await new LibraryStore(storePath).SaveAsync(initial);
        bool hold = false, fail = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<SearchDocument> latestDocuments = [];
        async Task<LocalSearchIndex> Build(IReadOnlyList<SearchDocument> documents, long revision, bool hide, CancellationToken token)
        {
            latestDocuments = documents.ToArray();
            if (fail) throw new InvalidOperationException("synthetic rebuild failure");
            if (hold) { hold = false; started.TrySetResult(); await release.Task; }
            return LocalSearchIndex.Build(documents, revision, hide);
        }
        using var model = new MainViewModel(storePath, searchIndexBuilder: Build);
        await model.InitializeAsync(); await model.EnsureSearchIndexAsync();
        check(model.IsSearchReady && model.SearchIndex!.Search("已存歌名", 40).Songs.Items.Count == 1,
            "索引读取全部已保存有效资料，无需歌曲先滚入视口或额外读取媒体标签");
        string id = model.Results.Single().Song.Id;
        model.SetPresentationScrolling(true);
        model.ApplyLocalMetadata(model.FindSong(id)!, media, new("已存歌名", "滚动艺人", "滚动专辑"));
        await model.EnsureSearchIndexAsync();
        check(model.SearchIndex!.Search("滚动艺人", 401).Songs.Items.Count == 1
            && model.SearchIndex.Search("滚动专辑", 402).Albums.Items.Count == 1,
            "滚动中仅艺人或专辑资料变更也独立更新索引，无需标题重排或滚动结束");
        model.SetPresentationScrolling(false);
        hold = true;
        await model.SaveCorrectionsAsync(id, "第一修正", "新艺人", "待清空专辑");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.SaveCorrectionsAsync(id, "最终歌名", "最终艺人", "");
        // A stale online completion is accepted into the existing details pipeline, but corrections win.
        await model.SaveOnlineMetadataAsync(id, new("过期线上歌名", "过期线上艺人", "过期线上专辑"));
        release.SetResult(); await model.EnsureSearchIndexAsync();
        var updated = model.SearchIndex!;
        check(updated.Search("最终艺人 最终歌名", 41).Songs.Items.Count == 1 && updated.Search("旧艺人", 42).Songs.Items.Count == 0
            && updated.Search("第一修正", 43).Songs.Items.Count == 0 && updated.Search("过期线上专辑", 44).Albums.Items.Count == 0,
            "首建或重建期间连续改名与清空字段时，仅最新有效资料可搜，旧线上值不能填回");
        check(model.IndexRevision == model.RequestedSearchIndexRevision, "旧构建晚到时不能覆盖新曲库修订");
        var full = LocalSearchIndex.Build(latestDocuments, model.IndexRevision, model.HideUnavailable);
        check(updated.Search("最终", 45, All).Songs.Items.Select(hit => hit.GroupId)
            .SequenceEqual(full.Search("最终", 46, All).Songs.Items.Select(hit => hit.GroupId)),
            "失效合并后的索引结果与当前有效资料全量重建一致");
        fail = true;
        await model.SaveCorrectionsAsync(id, "重建失败后的歌名", "最终艺人", "");
        await model.EnsureSearchIndexAsync();
        check(ReferenceEquals(updated, model.SearchIndex) && model.IsSearchReady && model.SearchIndexProblem.Length > 0,
            "后台重建失败保留上一可用快照，并暴露可重试错误");
        fail = false; await model.EnsureSearchIndexAsync();
        check(model.SearchIndex!.Search("重建失败后的歌名", 47).Songs.Items.Count == 1 && model.SearchIndexProblem.Length == 0,
            "显式重试可发布最新快照并清除构建错误");
        File.Delete(media); await model.RefreshAsync(); await model.EnsureSearchIndexAsync();
        check(model.SearchIndex!.Search("重建失败后的歌名", 48).Songs.Items.Count == 0, "所有来源离线后默认隐藏不可用搜索项");
        model.HideUnavailable = false; await model.EnsureSearchIndexAsync();
        var unavailable = model.SearchIndex!.Search("重建失败后的歌名", 49).Songs.Items.Single();
        check(!unavailable.IsAvailable && unavailable.Versions.All(version => !version.IsAvailable && version.PreferredResourceId is null),
            "关闭隐藏偏好后保留可辨认禁用项，不提供可入队资源");
        await File.WriteAllTextAsync(media, "synthetic search media"); await model.RefreshAsync(); await model.EnsureSearchIndexAsync();
        check(model.SearchIndex!.Search("重建失败后的歌名", 50).Songs.Items.Single().IsAvailable, "来源恢复并重扫后搜索可用状态随索引恢复");

        string firstBuildPath = Path.Combine(root, "search-first-build.json");
        await new LibraryStore(firstBuildPath).SaveAsync(initial);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool firstHold = true;
        async Task<LocalSearchIndex> HoldFirst(IReadOnlyList<SearchDocument> documents, long revision, bool hide, CancellationToken token)
        {
            if (firstHold) { firstHold = false; firstStarted.SetResult(); await firstRelease.Task; }
            return LocalSearchIndex.Build(documents, revision, hide);
        }
        using var firstModel = new MainViewModel(firstBuildPath, searchIndexBuilder: HoldFirst);
        await firstModel.InitializeAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(!firstModel.IsSearchReady && firstModel.IsSearchIndexBuilding, "首次索引未完成时提供准备中状态，不冒充已无匹配");
        await firstModel.SaveCorrectionsAsync(firstModel.Results.Single().Song.Id, "首建时改名", "首建艺人", "");
        firstRelease.SetResult(); await firstModel.EnsureSearchIndexAsync();
        check(firstModel.SearchIndex!.Search("首建时改名", 51).Songs.Items.Count == 1
            && firstModel.SearchIndex.Search("已存歌名", 52).Songs.Items.Count == 0
            && firstModel.IndexRevision == firstModel.RequestedSearchIndexRevision,
            "首次旧构建被人工延迟时，补建最新修正后才发布可搜索快照");

        bool failFirst = true;
        Task<LocalSearchIndex> FailFirst(IReadOnlyList<SearchDocument> documents, long revision, bool hide, CancellationToken token) =>
            failFirst ? Task.FromException<LocalSearchIndex>(new InvalidOperationException("synthetic first build failure"))
                : Task.FromResult(LocalSearchIndex.Build(documents, revision, hide));
        using var failedModel = new MainViewModel(Path.Combine(root, "search-first-failure.json"), searchIndexBuilder: FailFirst);
        await failedModel.InitializeAsync(); await failedModel.EnsureSearchIndexAsync();
        check(!failedModel.IsSearchReady && failedModel.SearchIndexProblem.Length > 0, "首次索引失败保留可重试错误，不当成空结果");
        failFirst = false; await failedModel.EnsureSearchIndexAsync();
        check(failedModel.IsSearchReady && failedModel.SearchIndexProblem.Length == 0, "首次构建失败后可显式重试恢复");
    }

    public static void Benchmark()
    {
        // Synthetic data only; each document represents a library entry. Explicit groups and resources
        // exercise version grouping without inferring identities from similar filenames or titles.
        SearchDocument[] documents = Enumerable.Range(0, 10000).Select(i =>
        {
            int group = i / 2;
            string title = (group % 10) switch { 0 => $"稻香 {group:D4}", 1 => $"夜航 {group:D4}", 2 => $"重庆之歌 {group:D4}",
                3 => $"Live Track {group:D4}", _ => $"共同歌曲 Track {group:D4}" };
            return Doc($"bench-{i:D5}", title, group % 3 == 0 ? "周杰伦" : $"测试艺人 {group % 97:D2}", $"共同专辑 {group % 71:D2}") with
            { GroupId = $"bench-group-{group:D4}", VersionId = $"bench-version-{i:D5}", VersionName = i % 2 == 0 ? "原版MV" : "现场版",
                IsOriginalMv = i % 2 == 0, AddedAt = Epoch.AddMinutes(i), Resources =
                [new($"resource-{i}-720", $"bench-{i:D5}", $"synthetic/{i}.mkv", true, 720),
                 new($"resource-{i}-4k", $"bench-{i:D5}", $"synthetic/{i}-4k.mkv", i % 5 != 0, 2160)] };
        }).ToArray();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long memoryBefore = GC.GetTotalMemory(true);
        long started = Stopwatch.GetTimestamp();
        var index = LocalSearchIndex.Build(documents, 100, true);
        double coldBuild = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long memoryAfter = GC.GetTotalMemory(true);
        string[] queries = ["稻香", "周杰倫", "zjl dx", "共同", "daoxiang", "重庆", "cq", "Live", "d", "co", "Track 0001", "稻香 现场版"];
        for (int i = 0; i < 24; i++) index.Search(queries[i % queries.Length], i);
        var measurements = new List<(string Query, double Milliseconds)>();
        for (int i = 0; i < 120; i++)
        {
            string query = queries[i % queries.Length];
            started = Stopwatch.GetTimestamp();
            var response = index.Search(query, 1000 + i);
            _ = response.Songs.Items.Count;
            measurements.Add((query, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        double Percentile(IEnumerable<double> source, double percentile)
        {
            var values = source.Order().ToArray();
            return Math.Round(values[(int)Math.Ceiling(values.Length * percentile) - 1], 3);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Mode = "search-core-only", Configuration =
#if DEBUG
                "Debug",
#else
                "Release",
#endif
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Framework = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription, Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            LogicalProcessors = Environment.ProcessorCount, DebuggerAttached = Debugger.IsAttached,
            Documents = documents.Length, Queries = measurements.Count, WarmupQueries = 24,
            ColdBuildMilliseconds = Math.Round(coldBuild, 3), RetainedIndexMemoryBytes = memoryAfter - memoryBefore,
            CoreP50Milliseconds = Percentile(measurements.Select(value => value.Milliseconds), .5),
            CoreP95Milliseconds = Percentile(measurements.Select(value => value.Milliseconds), .95),
            PerQuery = measurements.GroupBy(value => value.Query).Select(group => new { Query = group.Key, Runs = group.Count(),
                P50Milliseconds = Percentile(group.Select(value => value.Milliseconds), .5), P95Milliseconds = Percentile(group.Select(value => value.Milliseconds), .95) }),
            Limitations = "Synthetic in-memory data; measures index build and core search/grouping/paging only. No debounce, UI queue/binding/first-screen, real media IO or physical input. End-to-end P50/P95 remains a Windows manual/performance measurement."
        }, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(index); GC.KeepAlive(documents);
    }
}
