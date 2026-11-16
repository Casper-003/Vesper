using System.Globalization;
using System.Text.RegularExpressions;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using LyricifySearchSource = Lyricify.Lyrics.Searchers.Searchers;

namespace Vesper.Core;

public static class LyricifyService
{
    private static readonly string[] SidecarExtensions = [".qrc", ".yrc", ".krc", ".ttml", ".lrc"];
    private static readonly Regex LrcTimestampRegex = new(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex MetadataTagLineRegex = new(@"^\s*\[(?:id|hash|sign|total|offset|al|ar|ti|by|length|ver|version|re|ve|tool|language|encoding):[^\]]*\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedLyrics Parse(SongDetails details, string? mediaFilePath = null)
    {
        if (!details.LyricsManualOverride && TryReadSidecarLyrics(mediaFilePath, out string? sidecarRaw, out string? sidecarFormat)
            && !string.IsNullOrWhiteSpace(sidecarRaw))
        {
            var sidecarParsed = ParseRaw(sidecarRaw, details.OnlineLyrics?.TranslationLyrics, sidecarFormat);
            if (sidecarParsed.Lines.Count > 0) return sidecarParsed;
        }

        if (details.OnlineLyrics is { } online)
        {
            if (!string.IsNullOrWhiteSpace(online.WordLyricsFile))
            {
                var wordParsed = ParseRaw(online.WordLyricsFile, online.TranslationLyrics, online.RawFormat);
                if (wordParsed.Lines.Count > 0 && (wordParsed.HasWordSync || wordParsed.HasLineSync))
                    return wordParsed;
            }
            if (!string.IsNullOrWhiteSpace(online.SyncedLyrics))
            {
                var syncedParsed = ParseRaw(online.SyncedLyrics, online.TranslationLyrics);
                if (syncedParsed.Lines.Count > 0) return syncedParsed;
            }
            if (!string.IsNullOrWhiteSpace(online.PlainText))
            {
                var plainParsed = ParseRaw(online.PlainText, online.TranslationLyrics);
                if (plainParsed.Lines.Count > 0) return plainParsed;
            }
        }

        if (!string.IsNullOrWhiteSpace(details.Local?.Lyrics))
        {
            var localParsed = ParseRaw(details.Local.Lyrics);
            if (localParsed.Lines.Count > 0) return localParsed;
        }

        return new([], false, false, "None");
    }

    public static ParsedLyrics ParseRaw(string rawText, string? translationText = null, string? explicitFormat = null)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return new([], false, false, "None");

        LyricsData? data = null;
        string detectedFormat = explicitFormat ?? "Lrc";
        try
        {
            if (!string.IsNullOrWhiteSpace(explicitFormat)
                && Enum.TryParse<LyricsRawTypes>(explicitFormat, ignoreCase: true, out var rawType)
                && rawType != LyricsRawTypes.Unknown)
            {
                data = ParseHelper.ParseLyrics(rawText, rawType);
                detectedFormat = rawType.ToString();
            }
            if (data is null || data.Lines is null || data.Lines.Count == 0)
            {
                var autoType = TypeHelper.GetLyricsTypes(rawText);
                if (autoType != LyricsRawTypes.Unknown) detectedFormat = autoType.ToString();
                data = ParseHelper.ParseLyrics(rawText);
            }
        }
        catch
        {
            data = null;
        }

        var translations = ParseTranslationMap(translationText);
        if (data is { Lines.Count: > 0 })
        {
            var converted = ConvertLyricsData(data, translations, detectedFormat);
            if (converted.Lines.Count > 0) return converted;
        }

        var regexLrc = TryParseBasicLrc(rawText, translations);
        if (regexLrc.Lines.Count > 0) return regexLrc;

        string cleaned = LyricsDisplay.Clean(rawText);
        if (cleaned.Length == 0) return new([], false, false, "None");

        var plainLines = cleaned.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => new LyricsLine(0, 0, line, null, [], false))
            .ToArray();
        return plainLines.Length == 0
            ? new([], false, false, "None")
            : new(plainLines, HasWordSync: false, HasLineSync: false, SourceFormat: "PlainText");
    }

    public static bool ContainsWordTimings(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            var data = ParseHelper.ParseLyrics(raw);
            if (data?.Lines is null) return false;
            int syllableLines = 0;
            foreach (var line in data.Lines)
            {
                if (line is SyllableLineInfo { IsSyllable: true, Syllables.Count: > 1 })
                {
                    syllableLines++;
                    if (syllableLines >= 2) return true;
                }
            }
        }
        catch { /* Ignore parse errors during quick detection. */ }
        return false;
    }

    public static string ExtractPlainText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parsed = ParseRaw(raw);
        if (parsed.Lines.Count > 0)
            return string.Join('\n', parsed.Lines.Select(line => line.Text));
        return LyricsDisplay.Clean(raw);
    }

    public static string TryGenerateLrc(string rawText)
    {
        try
        {
            LyricsData? data = ParseHelper.ParseLyrics(rawText);
            if (data?.Lines is { Count: > 0 })
            {
                string? lrc = GenerateHelper.GenerateString(data, LyricsTypes.Lrc);
                if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
            }
        }
        catch { /* Ignore generation errors and return fallback. */ }
        return rawText;
    }

    public static async Task<IReadOnlyList<LyricsCandidate>> SearchCandidatesAsync(
        string title,
        string artist,
        string album,
        double durationSeconds,
        AdditionalSongSources? fallbackSources,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];

        string cleanTitle = SongNameMatching.CleanTitle(title);
        if (cleanTitle.Length == 0) cleanTitle = title.Trim();
        int? durationMs = durationSeconds > 0 ? (int)Math.Round(durationSeconds * 1000) : null;

        var metadata = new TrackMultiArtistMetadata
        {
            Title = cleanTitle,
            Artists = string.IsNullOrWhiteSpace(artist) ? [] : [artist.Trim()],
            Album = album?.Trim() ?? "",
            DurationMs = durationMs
        };

        var qqTask = SearchQQMusicAsync(metadata, cleanTitle, artist, durationSeconds, token);
        var neteaseTask = SearchNeteaseAsync(metadata, cleanTitle, artist, durationSeconds, token);
        var kugouTask = SearchKugouAsync(cleanTitle, artist, durationSeconds, token);
        var fallbackTask = fallbackSources is null
            ? Task.FromResult<IReadOnlyList<LyricsCandidate>>([])
            : SearchFallbackAsync(fallbackSources, cleanTitle, artist, token);

        await Task.WhenAll(qqTask, neteaseTask, kugouTask, fallbackTask).ConfigureAwait(false);

        var merged = new List<LyricsCandidate>();
        merged.AddRange(await qqTask.ConfigureAwait(false));
        merged.AddRange(await neteaseTask.ConfigureAwait(false));
        merged.AddRange(await kugouTask.ConfigureAwait(false));
        merged.AddRange(await fallbackTask.ConfigureAwait(false));

        return DeduplicateAndRank(merged, cleanTitle, artist, durationSeconds);
    }

    private static async Task<IReadOnlyList<LyricsCandidate>> SearchQQMusicAsync(
        TrackMultiArtistMetadata metadata, string title, string artist, double durationSeconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var searcher = SearchersHelper.GetSearcher(LyricifySearchSource.QQMusic);
            var results = await searcher.SearchForResults(metadata, false).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (results is null || results.Count == 0) return [];

            static string JoinArtists(string[]? artists) => artists is { Length: > 0 } ? string.Join(" / ", artists) : "";
            var picked = results
                .OfType<QQMusicSearchResult>()
                .Select(r => (Item: r, ArtistText: JoinArtists(r.Artists)))
                .Where(x => SongNameMatching.Same(x.Item.Title, title)
                    && (string.IsNullOrWhiteSpace(artist) || SongNameMatching.SameArtist(x.ArtistText, artist)))
                .OrderBy(x => x.Item.DurationMs is { } d && durationSeconds > 0 ? Math.Abs(d / 1000d - durationSeconds) : 0)
                .Take(2)
                .ToArray();

            var list = new List<LyricsCandidate>();
            foreach (var (item, artistText) in picked)
            {
                timeout.Token.ThrowIfCancellationRequested();
                string rawLyrics = "";
                string trans = "";
                if (!string.IsNullOrWhiteSpace(item.Id))
                {
                    var resp = await ProviderHelper.QQMusicApi.GetLyricsAsync(item.Id).WaitAsync(timeout.Token).ConfigureAwait(false);
                    rawLyrics = resp?.Lyrics ?? "";
                    trans = resp?.Trans ?? "";
                }
                if (string.IsNullOrWhiteSpace(rawLyrics) && !string.IsNullOrWhiteSpace(item.Mid))
                {
                    var qrcResp = await Lyricify.Lyrics.Decrypter.Qrc.Helper.GetLyricsByMidAsync(item.Mid).WaitAsync(timeout.Token).ConfigureAwait(false);
                    rawLyrics = qrcResp?.Lyrics ?? "";
                    if (string.IsNullOrWhiteSpace(trans)) trans = qrcResp?.Trans ?? "";
                }
                if (string.IsNullOrWhiteSpace(rawLyrics) && !string.IsNullOrWhiteSpace(item.Mid))
                {
                    var lrcResp = await ProviderHelper.QQMusicApi.GetLyric(item.Mid).WaitAsync(timeout.Token).ConfigureAwait(false);
                    lrcResp?.Decode();
                    rawLyrics = lrcResp?.Lyric ?? "";
                    if (string.IsNullOrWhiteSpace(trans)) trans = lrcResp?.Trans ?? "";
                }
                if (string.IsNullOrWhiteSpace(rawLyrics)) continue;

                bool hasWord = ContainsWordTimings(rawLyrics);
                string lrc = hasWord ? TryGenerateLrc(rawLyrics) : rawLyrics;
                string plain = ExtractPlainText(rawLyrics);
                if (plain.Length == 0) continue;

                long.TryParse(item.Id, out long numericId);
                double dur = item.DurationMs is { } ms ? ms / 1000d : 0;
                string url = !string.IsNullOrWhiteSpace(item.Mid)
                    ? $"https://y.qq.com/n/ryqq/songDetail/{item.Mid}"
                    : "https://y.qq.com/";

                list.Add(new LyricsCandidate(
                    numericId > 0 ? numericId : Math.Abs((item.Mid ?? item.Title).GetHashCode()),
                    item.Title, artistText, item.Album ?? "", dur,
                    plain, lrc, hasWord ? rawLyrics : "", hasWord, url)
                {
                    TranslationLyrics = trans,
                    RawFormat = hasWord ? "Qrc" : "Lrc"
                });
            }
            return list;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    private static async Task<IReadOnlyList<LyricsCandidate>> SearchNeteaseAsync(
        TrackMultiArtistMetadata metadata, string title, string artist, double durationSeconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var searcher = SearchersHelper.GetSearcher(LyricifySearchSource.Netease);
            var results = await searcher.SearchForResults(metadata, false).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (results is null || results.Count == 0) return [];

            static string JoinArtists(string[]? artists) => artists is { Length: > 0 } ? string.Join(" / ", artists) : "";
            var picked = results
                .OfType<NeteaseSearchResult>()
                .Select(r => (Item: r, ArtistText: JoinArtists(r.Artists)))
                .Where(x => SongNameMatching.Same(x.Item.Title, title)
                    && (string.IsNullOrWhiteSpace(artist) || SongNameMatching.SameArtist(x.ArtistText, artist)))
                .OrderBy(x => x.Item.DurationMs is { } d && durationSeconds > 0 ? Math.Abs(d / 1000d - durationSeconds) : 0)
                .Take(2)
                .ToArray();

            var list = new List<LyricsCandidate>();
            foreach (var (item, artistText) in picked)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                var resp = await ProviderHelper.NeteaseApi.GetLyricNew(item.Id).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (resp is null) continue;

                string yrc = resp.Yrc?.Lyric ?? "";
                string lrc = resp.Lrc?.Lyric ?? "";
                string trans = resp.Ytlrc?.Lyric ?? resp.Tlyric?.Lyric ?? "";
                if (string.IsNullOrWhiteSpace(yrc) && string.IsNullOrWhiteSpace(lrc)) continue;

                bool hasWord = ContainsWordTimings(yrc);
                string primaryForPlain = hasWord ? yrc : lrc;
                string plain = ExtractPlainText(primaryForPlain);
                if (plain.Length == 0) continue;

                string syncedLrc = !string.IsNullOrWhiteSpace(lrc) ? lrc : hasWord ? TryGenerateLrc(yrc) : "";
                long.TryParse(item.Id, out long numericId);
                double dur = item.DurationMs is { } ms ? ms / 1000d : 0;

                list.Add(new LyricsCandidate(
                    numericId, item.Title, artistText, item.Album ?? "", dur,
                    plain, syncedLrc, hasWord ? yrc : "", hasWord,
                    $"https://music.163.com/song?id={item.Id}")
                {
                    TranslationLyrics = trans,
                    RawFormat = hasWord ? "Yrc" : "Lrc"
                });
            }
            return list;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    private static async Task<IReadOnlyList<LyricsCandidate>> SearchKugouAsync(
        string title, string artist, double durationSeconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            string keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{artist.Trim()} - {title}";
            int? durationMs = durationSeconds > 0 ? (int)Math.Round(durationSeconds * 1000) : null;
            var resp = await ProviderHelper.KugouApi.GetSearchLyrics(keyword, durationMs, "").WaitAsync(timeout.Token).ConfigureAwait(false);
            if (resp?.Candidates is null || resp.Candidates.Count == 0) return [];

            var picked = resp.Candidates
                .Where(c => SongNameMatching.Same(c.Song, title)
                    && (string.IsNullOrWhiteSpace(artist) || SongNameMatching.SameArtist(c.Singer, artist)))
                .Where(c => durationSeconds <= 0 || Math.Abs(c.Duration / 1000d - durationSeconds) <= 8)
                .OrderBy(c => durationSeconds > 0 ? Math.Abs(c.Duration / 1000d - durationSeconds) : 0)
                .ThenByDescending(c => c.Score)
                .Take(2)
                .ToArray();

            var list = new List<LyricsCandidate>();
            foreach (var cand in picked)
            {
                timeout.Token.ThrowIfCancellationRequested();
                string? rawKrc = await Lyricify.Lyrics.Decrypter.Krc.Helper.GetLyricsAsync(cand.Id, cand.AccessKey).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawKrc)) continue;

                bool hasWord = ContainsWordTimings(rawKrc);
                string lrc = TryGenerateLrc(rawKrc);
                string plain = ExtractPlainText(rawKrc);
                if (plain.Length == 0) continue;

                long.TryParse(cand.Id, out long numericId);
                list.Add(new LyricsCandidate(
                    numericId > 0 ? numericId : Math.Abs(cand.Id.GetHashCode()),
                    string.IsNullOrWhiteSpace(cand.Song) ? title : cand.Song,
                    string.IsNullOrWhiteSpace(cand.Singer) ? artist : cand.Singer,
                    "", cand.Duration / 1000d,
                    plain, lrc, hasWord ? rawKrc : "", hasWord, "https://www.kugou.com/")
                {
                    RawFormat = hasWord ? "Krc" : "Lrc"
                });
            }
            return list;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    private static async Task<IReadOnlyList<LyricsCandidate>> SearchFallbackAsync(
        AdditionalSongSources sources, string title, string artist, CancellationToken token)
    {
        try { return await sources.SearchLyricsWithArtist(title, artist, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    private static IReadOnlyList<LyricsCandidate> DeduplicateAndRank(
        IEnumerable<LyricsCandidate> candidates, string title, string artist, double durationSeconds)
    {
        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.PlainText) || !string.IsNullOrWhiteSpace(c.SyncedLyrics) || !string.IsNullOrWhiteSpace(c.WordLyricsFile))
            .OrderByDescending(c => SongNameMatching.Same(c.Title, title))
            .ThenByDescending(c => string.IsNullOrWhiteSpace(artist) || SongNameMatching.SameArtist(c.Artist, artist))
            .ThenByDescending(c => c.HasWordSync)
            .ThenByDescending(c => !string.IsNullOrWhiteSpace(c.SyncedLyrics) || !string.IsNullOrWhiteSpace(c.WordLyricsFile))
            .ThenBy(c => durationSeconds > 0 && c.DurationSeconds > 0 ? Math.Abs(c.DurationSeconds - durationSeconds) : 20d)
            .ThenByDescending(c => !string.IsNullOrWhiteSpace(c.TranslationLyrics))
            .DistinctBy(c => $"{c.SourceUrl}|{c.HasWordSync}|{c.Title}|{c.Artist}")
            .ToArray();
    }

    private static bool TryReadSidecarLyrics(string? mediaFilePath, out string? raw, out string? format)
    {
        raw = null;
        format = null;
        if (string.IsNullOrWhiteSpace(mediaFilePath)) return false;
        try
        {
            string? dir = Path.GetDirectoryName(mediaFilePath);
            string stem = Path.GetFileNameWithoutExtension(mediaFilePath);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem)) return false;

            foreach (string ext in SidecarExtensions)
            {
                string candidate = Path.Combine(dir, stem + ext);
                if (!File.Exists(candidate)) continue;
                raw = File.ReadAllText(candidate);
                format = ext.TrimStart('.') switch
                {
                    "qrc" => "Qrc",
                    "yrc" => "Yrc",
                    "krc" => "Krc",
                    "ttml" => "Ttml",
                    _ => "Lrc"
                };
                return true;
            }
        }
        catch { /* Ignore local IO issues and fall back cleanly. */ }
        return false;
    }

    private static ParsedLyrics ConvertLyricsData(LyricsData data, List<(int TimeMs, string Text)> translations, string detectedFormat)
    {
        var rawLines = new List<LyricsLine>();
        bool anyWordSync = false;
        bool anyLineSync = false;

        if (data?.Lines is null) return new([], false, false, "None");
        foreach (ILineInfo line in data.Lines)
        {
            if (line is SyllableLineInfo { IsSyllable: true, Syllables.Count: > 0 } sylLine)
            {
                var syllables = new List<LyricsSyllable>(sylLine.Syllables.Count);
                foreach (ISyllableInfo syl in sylLine.Syllables)
                {
                    if (string.IsNullOrEmpty(syl.Text)) continue;
                    syllables.Add(new LyricsSyllable(syl.StartTime, Math.Max(30, syl.Duration), syl.Text));
                }
                if (syllables.Count == 0) continue;
                string joined = string.Concat(syllables.Select(s => s.Text)).Trim();
                if (joined.Length == 0 || MetadataTagLineRegex.IsMatch(joined)) continue;

                int startMs = line.StartTime ?? syllables[0].StartMs;
                int endMs = line.EndTime ?? (syllables[^1].StartMs + syllables[^1].DurationMs);
                string? trans = CleanInlineTranslation(line.SubLine?.Text) ?? MatchTranslation(startMs, translations);

                rawLines.Add(new LyricsLine(startMs, Math.Max(startMs, endMs), joined, trans, syllables, IsWordSynced: true));
                anyWordSync = true;
                anyLineSync = true;
                continue;
            }

            string text = LyricsDisplay.Clean(line.Text ?? "");
            if (text.Length == 0) continue;

            if (line.StartTime is int lineStart)
            {
                int lineEnd = line.EndTime is int explicitEnd && explicitEnd > lineStart ? explicitEnd : 0;
                string? trans = CleanInlineTranslation(line.SubLine?.Text) ?? MatchTranslation(lineStart, translations);
                rawLines.Add(new LyricsLine(lineStart, lineEnd, text, trans, [], IsWordSynced: false));
                anyLineSync = true;
            }
            else if (!anyLineSync)
            {
                rawLines.Add(new LyricsLine(0, 0, text, CleanInlineTranslation(line.SubLine?.Text), [], IsWordSynced: false));
            }
        }

        if (!anyLineSync)
        {
            return rawLines.Count == 0
                ? new([], false, false, "None")
                : new(rawLines, HasWordSync: false, HasLineSync: false, SourceFormat: "PlainText");
        }

        // 合并相同时间戳的双语行（当 LRC 同时交错放入原文与翻译行且未显式拆分时）。
        var finalized = MergeDuplicateTimestampsAndFillDurations(rawLines);
        return new ParsedLyrics(finalized, anyWordSync, HasLineSync: true, SourceFormat: detectedFormat);
    }

    private static List<LyricsLine> MergeDuplicateTimestampsAndFillDurations(List<LyricsLine> source)
    {
        var merged = new List<LyricsLine>(source.Count);
        foreach (var line in source.OrderBy(l => l.StartMs))
        {
            if (merged.Count > 0 && Math.Abs(merged[^1].StartMs - line.StartMs) <= 40
                && !merged[^1].IsWordSynced && !line.IsWordSynced
                && string.IsNullOrEmpty(merged[^1].Translation)
                && !string.Equals(merged[^1].Text, line.Text, StringComparison.OrdinalIgnoreCase))
            {
                merged[^1] = merged[^1] with { Translation = line.Text };
                continue;
            }
            merged.Add(line);
        }

        for (int i = 0; i < merged.Count; i++)
        {
            var cur = merged[i];
            if (cur.EndMs > cur.StartMs) continue;
            int inferredEnd = i + 1 < merged.Count
                ? Math.Max(cur.StartMs + 400, merged[i + 1].StartMs)
                : cur.StartMs + 5000;
            merged[i] = cur with { EndMs = inferredEnd };
        }
        return merged;
    }

    private static ParsedLyrics TryParseBasicLrc(string rawText, List<(int TimeMs, string Text)> translations)
    {
        var lines = new List<LyricsLine>();
        foreach (string rawLine in rawText.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var matches = LrcTimestampRegex.Matches(rawLine);
            if (matches.Count == 0) continue;
            string body = LrcTimestampRegex.Replace(rawLine, "").Trim();
            if (body.Length == 0 || MetadataTagLineRegex.IsMatch(body)) continue;

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string fracText = m.Groups[3].Value;
                int ms = fracText.Length switch
                {
                    1 => int.Parse(fracText, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fracText, CultureInfo.InvariantCulture) * 10,
                    >= 3 => int.Parse(fracText[..3], CultureInfo.InvariantCulture),
                    _ => 0
                };
                int totalMs = (min * 60 + sec) * 1000 + ms;
                lines.Add(new LyricsLine(totalMs, 0, body, MatchTranslation(totalMs, translations), [], IsWordSynced: false));
            }
        }
        if (lines.Count == 0) return new([], false, false, "None");
        return new(MergeDuplicateTimestampsAndFillDurations(lines), HasWordSync: false, HasLineSync: true, SourceFormat: "Lrc");
    }

    private static List<(int TimeMs, string Text)> ParseTranslationMap(string? translationText)
    {
        var list = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(translationText)) return list;
        foreach (string rawLine in translationText.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var matches = LrcTimestampRegex.Matches(rawLine);
            if (matches.Count == 0) continue;
            string body = LyricsDisplay.Clean(LrcTimestampRegex.Replace(rawLine, "").Trim());
            if (body.Length == 0 || body == "//") continue;
            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string fracText = m.Groups[3].Value;
                int ms = fracText.Length switch
                {
                    1 => int.Parse(fracText, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fracText, CultureInfo.InvariantCulture) * 10,
                    >= 3 => int.Parse(fracText[..3], CultureInfo.InvariantCulture),
                    _ => 0
                };
                list.Add(((min * 60 + sec) * 1000 + ms, body));
            }
        }
        return list;
    }

    private static string? MatchTranslation(int timeMs, List<(int TimeMs, string Text)> translations)
    {
        if (translations.Count == 0) return null;
        string? best = null;
        int bestDiff = 450;
        foreach (var entry in translations)
        {
            int diff = Math.Abs(entry.TimeMs - timeMs);
            if (diff < bestDiff) { bestDiff = diff; best = entry.Text; }
        }
        return best;
    }

    private static string? CleanInlineTranslation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string cleaned = LyricsDisplay.Clean(text);
        return cleaned.Length == 0 || cleaned == "//" ? null : cleaned;
    }
}
