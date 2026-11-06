using Vesper.App;
using Vesper.Core;

internal static class SongAssetStateChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        CheckIdentityAndCooldown(check);
        await CheckPublicationAndPersistenceAsync(root, check);
    }

    private static void CheckIdentityAndCooldown(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var input = new SongLookupInput("Artist - Song.mp3", new(Local: new("Song", "Artist", "Album"),
            Online: new("Song", "Artist", "Album", CoverUrl: "https://coverartarchive.org/release/one/front-500")));
        string key = SongAssetState.Identity(input);
        var lyricsOnly = input with { Details = input.Details with
        {
            OnlineLyrics = new("Lyrics", "", "", false, "https://lrclib.net/api/get/1", "Song", "Artist", 180, true),
            Lookup = new(input.Key, now, LookupOutcome.Matched, LookupOutcome.Matched)
        } };
        var changedCover = input with { Details = input.Details with
            { Online = input.Details.Online! with { CoverUrl = "https://coverartarchive.org/release/two/front-500" } } };
        var changedCredits = input with { Details = input.Details with
            { Online = input.Details.Online! with { ArtistCredits = [new("Composer", "composer")] } } };
        var changedIdentity = input with { Details = input.Details with
            { Corrections = new("Different song", "Other artist", "", now) } };
        check(SongAssetState.Identity(lyricsOnly) == key && SongAssetState.Identity(changedCover) != key
            && SongAssetState.Identity(changedCredits) != key && SongAssetState.Identity(changedIdentity) != key,
            "图片同步身份随曲目、封面和作者归属变化，单独歌词与请求时间不触发图片重采");
        var failed = new SongAssetState(key, now, true);
        var completed = new SongAssetState(key, now, false);
        var missing = completed with { MissingArtwork = true, CreditsCheckedAt = now };
        check(!failed.ShouldRefresh(key, now.AddSeconds(59)) && failed.ShouldRefresh(key, now.AddMinutes(1))
            && !completed.ShouldRefresh(key, now.AddDays(29)) && completed.ShouldRefresh(key, now.AddDays(30))
            && completed.ShouldRefresh(SongAssetState.Identity(changedCover), now),
            "图片失败至少一分钟重试、成功结果复用三十天，新图片身份可立即同步");
        check(!missing.ShouldRefresh(key, now.AddHours(23)) && missing.ShouldRefresh(key, now.AddDays(1))
            && missing.CreditsCheckedAt == now,
            "已确认缺图保留一天负缓存，作曲资料成功时间独立于缺图重试保留");
        var artist = new MusicExternalId("musicbrainz", "artist", "11111111-1111-1111-1111-111111111111");
        var empty = new CachedArtistProfile(artist, null, now);
        var unavailable = empty with { Unavailable = true };
        var found = empty with { Profile = new("Artist", "Biography", "", "https://musicbrainz.org/artist/" + artist.Value),
            ImageLookupVersion = CachedArtistProfile.CurrentImageLookupVersion };
        check(!empty.ShouldRefresh(now.AddHours(23)) && empty.ShouldRefresh(now.AddDays(1))
            && !unavailable.ShouldRefresh(now.AddSeconds(59)) && unavailable.ShouldRefresh(now.AddMinutes(1))
            && !found.ShouldRefresh(now.AddDays(29)) && found.ShouldRefresh(now.AddDays(30)),
            "作者查无资料、网络失败和成功结果独立保留一天、一分钟和三十天冷却");
        check((found with { ImageLookupVersion = 0 }).ShouldRefresh(now)
            && !found.ShouldRefresh(now.AddMinutes(1))
            && !(found with { ImageLookupVersion = 0, Profile = found.Profile! with { ImageUrl = "https://thumb.wikimedia.org/portrait.jpg" } }).ShouldRefresh(now),
            "旧版空图片成功结果升级后重查一次，已升级部分结果及有效图片不反复请求");
        check(!(found with { ImageLookupVersion = 0, Unavailable = true }).ShouldRefresh(now.AddSeconds(59)),
            "空图片升级期间的网络失败仍遵守一分钟冷却");
    }

    private static async Task CheckPublicationAndPersistenceAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "song-assets-media"), store = Path.Combine(root, "song-assets.json");
        Directory.CreateDirectory(folder);
        string media = Path.Combine(folder, "AssetSong.mp3");
        await File.WriteAllTextAsync(media, "asset publication fixture media");
        var performerId = new MusicExternalId("musicbrainz", "artist", "11111111-1111-1111-1111-111111111111");
        var onlinePerformerId = new MusicExternalId("musicbrainz", "artist", "22222222-2222-2222-2222-222222222222");
        var composerId = new MusicExternalId("musicbrainz", "artist", "33333333-3333-3333-3333-333333333333");
        var composer = new MusicArtistCredit("Composer", "composer", [composerId]);
        var profile = new CachedArtistProfile(composerId,
            new("Composer", "作者介绍", "https://upload.wikimedia.org/composer.jpg", "https://musicbrainz.org/artist/" + composerId.Value),
            DateTimeOffset.UtcNow);
        string songId, sourceKey;
        using (var model = new MainViewModel(store))
        {
            await model.InitializeAsync();
            await model.AddDirectoryAsync(folder);
            songId = model.Results.Single().Song.Id;
            sourceKey = model.GetEnrichmentSourceKey(songId)!;
            await model.SaveBackgroundLocalMetadataAsync(songId, sourceKey, new(media,
                new("AssetSong", "Singer", "Album", Lyrics: "Local lyrics")
                { ArtistCredits = [new("Singer", ExternalIds: [performerId])] }), () => true);
            var localInput = model.GetLookupInput(songId)!;
            await model.SaveBackgroundAutomaticLookupAsync(songId, localInput,
                new(new("AssetSong", "Singer", "Album", CoverUrl: "https://coverartarchive.org/release/one/front-500")
                    { ArtistCredits = [new("Singer", ExternalIds: [onlinePerformerId])] }, null,
                    LookupOutcome.Matched, LookupOutcome.Matched), () => true);
            var expected = model.GetLookupInput(songId)!;
            check(!await model.SaveBackgroundAssetsAsync(songId, sourceKey, expected, [composer], [profile], false, () => false)
                && model.Library.Details[songId].Assets is null && model.GetCachedArtistProfile(composerId) is null,
                "失效发布票据不能写入图片完成标记、作曲家归属或作者资料缓存");
            check(await model.SaveBackgroundAssetsAsync(songId, sourceKey, expected, [composer], [profile], false, () => true),
                "有效来源和曲目身份允许保存后台作者资料与作曲家图片关联");
            var effective = model.GetSongMetadata(songId);
            check(effective.ArtistCredits.Single(credit => credit.Role == "performer").ExternalIds!.Single() == performerId
                && effective.ArtistCredits.Single(credit => credit.Role == "composer").ExternalIds!.Single() == composerId,
                "本地演唱艺人优先身份保持原样，同时保留联网补充的作曲家身份");
            var current = model.GetLookupInput(songId)!;
            check(model.Library.Details[songId].Assets?.Key == SongAssetState.Identity(current)
                && model.GetCachedArtistProfile(composerId)?.Profile?.ImageUrl == profile.Profile!.ImageUrl,
                "图片完成标记绑定补充作者后的最终身份，作者图片URL与出处分开持久保存");
            check(await model.FlushBackgroundMetadataAsync(), "作者、图片同步标记和作曲家归属可合并落盘");
        }
        using (var restored = new MainViewModel(store))
        {
            await restored.InitializeAsync();
            var expected = restored.GetLookupInput(songId)!;
            check(restored.GetCachedArtistProfile(composerId)?.Profile == profile.Profile
                && restored.GetSongMetadata(songId).ArtistCredits.Any(credit => credit.Role == "composer" && credit.Name == "Composer")
                && restored.Library.Details[songId].Assets is { } saved
                && !saved.ShouldRefresh(SongAssetState.Identity(expected), DateTimeOffset.UtcNow),
                "重启恢复作者介绍、作曲家身份、图片来源和同步标记，未变化内容不需重新联网");
            await restored.SaveCorrectionsAsync(songId, "New identity", "New artist", "");
            check(!await restored.SaveBackgroundAssetsAsync(songId, sourceKey, expected,
                    [new("Stale composer", "composer")], [profile with { Profile = profile.Profile! with { Description = "过期资料" } }], false, () => true)
                && !restored.GetSongMetadata(songId).ArtistCredits.Any(credit => credit.Name == "Stale composer")
                && restored.GetCachedArtistProfile(composerId)?.Profile?.Description == "作者介绍",
                "手动更正曲目身份后，旧后台图片结果不能追加旧作者或覆盖资料缓存");
            var corrected = restored.GetLookupInput(songId)!;
            await restored.RemoveDirectoryAsync(folder);
            check(!await restored.SaveBackgroundAssetsAsync(songId, sourceKey, corrected,
                    [new("Removed composer", "composer")], [profile with { Profile = profile.Profile! with { Description = "移除后资料" } }], false, () => true)
                && restored.GetEnrichmentSourceKey(songId) is null
                && restored.GetCachedArtistProfile(composerId)?.Profile?.Description == "作者介绍",
                "来源移除后，即使任务忽略取消也不能发布作者归属或图片完成记录");
        }
    }
}
