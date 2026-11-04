namespace Vesper.Core;

/// <summary>An immutable, precomputed in-memory library snapshot; querying performs no IO.</summary>
public sealed class LocalSearchIndex
{
    private sealed record Field(SearchField Kind, SearchKey Key);
    private sealed record Version(string Id, string SongId, string GroupId, string Title, string Artist,
        string Name, DateTimeOffset AddedAt, bool Original, IReadOnlyList<SearchResource> Resources,
        IReadOnlyList<Field> Fields, IReadOnlyList<SearchArtistCredit> Credits, string Album, string AlbumId, string AlbumArtist)
    {
        public string SortArtist { get; } = LocalSearchText.Normalize(Artist);
        public bool Available => Resources.Any(resource => resource.IsAvailable);
        public SearchResource? Preferred => Resources.Where(resource => resource.IsAvailable)
            .OrderByDescending(resource => resource.Resolution).ThenBy(resource => resource.SourceOrder)
            .ThenBy(resource => resource.Id, StringComparer.Ordinal).ThenBy(resource => resource.Path, StringComparer.Ordinal).FirstOrDefault();
    }
    private sealed record Group(string Id, IReadOnlyList<Version> Versions, Version Representative);
    private sealed record Entity(string Id, string Name, string Artist, IReadOnlyList<Field> Fields, int Count);
    private readonly IReadOnlyList<Group> groups;
    private readonly IReadOnlyList<Entity> artists;
    private readonly IReadOnlyList<Entity> albums;
    public long Revision { get; }
    public bool HideUnavailable { get; }
    public int DocumentCount { get; }
    private LocalSearchIndex(long revision, bool hideUnavailable, int count,
        IReadOnlyList<Group> groups, IReadOnlyList<Entity> artists, IReadOnlyList<Entity> albums)
    { Revision = revision; HideUnavailable = hideUnavailable; DocumentCount = count; this.groups = groups; this.artists = artists; this.albums = albums; }

    public static LocalSearchIndex Build(IEnumerable<SearchDocument> documents, long revision, bool hideUnavailable,
        CancellationToken cancellationToken = default)
    {
        var docs = documents.ToArray();
        if (docs.Any(document => string.IsNullOrWhiteSpace(document.SongId))) throw new ArgumentException("Every search document needs a stable song ID.", nameof(documents));
        var fields = new Dictionary<(SearchField, string), Field>();
        Field Key(SearchField kind, string text)
        {
            if (!fields.TryGetValue((kind, text), out var field))
                fields[(kind, text)] = field = new(kind, SearchKey.Create(text, kind is SearchField.Title or SearchField.Artist));
            return field;
        }
        var versions = new List<Version>();
        // Only explicit identities combine entries. Similar titles/paths and equally named live versions do not.
        foreach (var versionDocs in docs.GroupBy(document => (Group: Identity(document.GroupId, document.SongId), Version: Identity(document.VersionId, document.SongId))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = versionDocs.OrderBy(document => document.SongId, StringComparer.Ordinal).ToArray();
            var first = ordered[0];
            SearchResource[] resources = ordered.SelectMany(document => document.Resources is { Count: > 0 } supplied
                    ? supplied : [new SearchResource(document.SongId, document.SongId, "", document.IsAvailable)])
                .GroupBy(resource => (resource.Id, resource.Path)).Select(group => group.OrderByDescending(resource => resource.IsAvailable)
                    .ThenByDescending(resource => resource.Resolution).ThenBy(resource => resource.SourceOrder).First()).ToArray();
            var credits = (first.Artists ?? (first.Artist.Length > 0 ? new[] { new SearchArtistCredit(first.Artist) } : []))
                .Where(credit => !string.IsNullOrWhiteSpace(credit.Name)).DistinctBy(credit => Identity(credit.Id, LocalSearchText.Normalize(credit.Name))).ToArray();
            var recordFields = new List<Field> { Key(SearchField.Title, first.Title) };
            recordFields.AddRange(credits.Select(credit => Key(SearchField.Artist, credit.Name)));
            if (first.Artist.Length > 0 && !credits.Any(credit => credit.Name == first.Artist)) recordFields.Add(Key(SearchField.Artist, first.Artist));
            if (first.Album.Length > 0) recordFields.Add(Key(SearchField.Album, first.Album));
            string albumArtist = first.AlbumArtist.Length > 0 ? first.AlbumArtist : first.Artist;
            // Without trusted release identity, stay conservative; never merge two artists' same-named releases.
            string albumId = Identity(first.AlbumId, $"album:{LocalSearchText.Fold(LocalSearchText.Normalize(first.Album))}:{LocalSearchText.Fold(LocalSearchText.Normalize(albumArtist))}");
            if (albumArtist.Length == 0 && string.IsNullOrWhiteSpace(first.AlbumId)) albumId += ":" + first.SongId;
            versions.Add(new(versionDocs.Key.Version, first.SongId, versionDocs.Key.Group, first.Title, first.Artist,
                first.VersionName, ordered.Min(document => document.AddedAt), ordered.Any(document => document.IsOriginalMv),
                Array.AsReadOnly(resources), recordFields.AsReadOnly(), Array.AsReadOnly(credits), first.Album, albumId, albumArtist));
        }
        var groups = versions.GroupBy(version => version.GroupId).Select(group =>
        {
            var members = group.OrderBy(version => version.Id, StringComparer.Ordinal).ToArray();
            var available = members.Where(version => version.Available).ToArray();
            var representative = (available.Length > 0 ? available : members).OrderByDescending(version => version.Original)
                .ThenBy(version => version.AddedAt).ThenBy(version => version.Id, StringComparer.Ordinal).First();
            return new Group(group.Key, Array.AsReadOnly(members), representative);
        }).OrderBy(group => group.Id, StringComparer.Ordinal).ToArray();
        var visible = versions.Where(version => !hideUnavailable || version.Available).ToArray();
        var artists = visible.SelectMany(version => version.Credits.Select(credit => (Version: version, Credit: credit)))
            .GroupBy(pair => Identity(pair.Credit.Id, "artist:" + LocalSearchText.Fold(LocalSearchText.Normalize(pair.Credit.Name))))
            .Select(group => new Entity(group.Key, group.First().Credit.Name, "",
                new[] { Key(SearchField.Artist, group.First().Credit.Name) }, group.Select(pair => pair.Version.GroupId).Distinct().Count())).ToArray();
        var albums = visible.Where(version => version.Album.Length > 0).GroupBy(version => version.AlbumId)
            .Select(group =>
            {
                var first = group.First();
                var albumFields = new List<Field> { Key(SearchField.Album, first.Album) };
                if (first.AlbumArtist.Length > 0) albumFields.Add(Key(SearchField.Artist, first.AlbumArtist));
                return new Entity(group.Key, first.Album, first.AlbumArtist, albumFields.AsReadOnly(), group.Select(version => version.GroupId).Distinct().Count());
            }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(revision, hideUnavailable, docs.Length, Array.AsReadOnly(groups), Array.AsReadOnly(artists), Array.AsReadOnly(albums));
    }

    private static string Identity(string? id, string fallback) => string.IsNullOrWhiteSpace(id) ? fallback : id;

    public SearchResponse Search(string query, long queryId, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        var prepared = new Query(query);
        if (prepared.Whole.Literal.Length == 0) return new(query, queryId, Revision, SearchStatus.Empty, [], [], [], options);
        var songs = new List<(SongGroupHit Hit, Path Path, string SortTitle, string SortArtist)>();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HideUnavailable && !group.Versions.Any(version => version.Available)) continue;
            var paths = new Dictionary<string, Path>();
            foreach (var version in group.Versions)
            {
                if (HideUnavailable && !version.Available) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (FindPath(prepared, version.Fields, version.Id, version.Name, version.Original, cancellationToken) is { } path) paths[version.Id] = path;
            }
            if (paths.Count == 0) continue;
            var matched = group.Versions.Where(version => paths.ContainsKey(version.Id))
                .OrderBy(version => paths[version.Id], PathComparer.Instance)
                .ThenByDescending(version => version.Id == group.Representative.Id)
                .ThenByDescending(version => version.Available).ThenBy(version => version.AddedAt)
                .ThenBy(version => version.Id, StringComparer.Ordinal).ToArray();
            var best = matched[0];
            var children = group.Versions.Where(version => !HideUnavailable || version.Available)
                .OrderBy(version => paths.ContainsKey(version.Id) ? 0 : 1)
                .ThenBy(version => paths.GetValueOrDefault(version.Id), PathComparer.Instance)
                .ThenByDescending(version => version.Id == group.Representative.Id)
                .ThenByDescending(version => version.Available).ThenBy(version => version.AddedAt)
                .ThenBy(version => version.Id, StringComparer.Ordinal).Select(version =>
                {
                    var resource = version.Preferred;
                    return new VersionHit(version.Id, resource?.SongId ?? version.SongId, version.Name, version.Available,
                        resource?.Id, resource?.Resolution ?? 0, paths.GetValueOrDefault(version.Id)?.Evidence)
                    { Resources = version.Resources, AddedAt = version.AddedAt, IsOriginalMv = version.Original };
                }).ToArray();
            songs.Add((new(group.Id, group.Representative.Title, group.Representative.Artist, group.Representative.Id, best.Id,
                Array.AsReadOnly(matched.Select(version => version.Id).ToArray()), Array.AsReadOnly(children), group.Versions.Any(version => version.Available), paths[best.Id].Evidence), paths[best.Id],
                group.Representative.Fields.First(field => field.Kind == SearchField.Title).Key.Literal,
                group.Representative.SortArtist));
        }
        songs.Sort((left, right) =>
        {
            int result = PathComparer.Instance.Compare(left.Path, right.Path);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.SortTitle, right.SortTitle);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.SortArtist, right.SortArtist);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Hit.GroupId, right.Hit.GroupId);
        });
        var artistHits = MatchEntities(artists, prepared, cancellationToken).Select(pair => new ArtistHit(pair.Entity.Id, pair.Entity.Name, pair.Path.Evidence, pair.Entity.Count)).ToArray();
        var albumHits = MatchEntities(albums, prepared, cancellationToken).Select(pair => new AlbumHit(pair.Entity.Id, pair.Entity.Name, pair.Entity.Artist, pair.Path.Evidence, pair.Entity.Count)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(query, queryId, Revision, songs.Count + artistHits.Length + albumHits.Length == 0 ? SearchStatus.NoMatch : SearchStatus.Success,
            Array.AsReadOnly(songs.Select(pair => pair.Hit).ToArray()), Array.AsReadOnly(artistHits), Array.AsReadOnly(albumHits), options);
    }

    private static IReadOnlyList<(Entity Entity, Path Path)> MatchEntities(IReadOnlyList<Entity> entities, Query query, CancellationToken token)
    {
        var hits = new List<(Entity Entity, Path Path)>();
        foreach (var entity in entities)
        {
            token.ThrowIfCancellationRequested();
            if (FindPath(query, entity.Fields, "", "", false, token) is { } path) hits.Add((entity, path));
        }
        hits.Sort((left, right) =>
        {
            int result = PathComparer.Instance.Compare(left.Path, right.Path);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Entity.Fields[0].Key.Literal, right.Entity.Fields[0].Key.Literal);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Entity.Id, right.Entity.Id);
        });
        return hits;
    }

    private sealed class Query
    {
        public SearchKey Whole { get; }
        public IReadOnlyList<SearchKey> Terms { get; }
        public Query(string text)
        {
            Whole = SearchKey.Create(text);
            Terms = Whole.Literal.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(term => SearchKey.Create(term)).ToArray();
        }
    }
    private sealed record Evidence(SearchTermEvidence Term, int Kind, int Extra);
    private sealed record Path(int Rank, int TransliterationPrefix, int Exact, int TitleTerms, int FoldedTerms, int Extra,
        MatchEvidence Evidence);
    private sealed class PathComparer : IComparer<Path?>
    {
        public static readonly PathComparer Instance = new();
        public int Compare(Path? left, Path? right)
        {
            if (left is null) return right is null ? 0 : 1;
            if (right is null) return -1;
            int result = left.Rank.CompareTo(right.Rank);
            if (result != 0) return result;
            result = left.TransliterationPrefix.CompareTo(right.TransliterationPrefix);
            if (result != 0) return result;
            result = right.Exact.CompareTo(left.Exact);
            if (result != 0) return result;
            result = right.TitleTerms.CompareTo(left.TitleTerms);
            if (result != 0) return result;
            result = left.FoldedTerms.CompareTo(right.FoldedTerms);
            return result != 0 ? result : left.Extra.CompareTo(right.Extra);
        }
    }
    private static Path? FindPath(Query query, IReadOnlyList<Field> fields, string versionId, string versionName, bool original, CancellationToken token)
    {
        Path? best = null;
        void Consider(Path? candidate)
        { if (candidate is not null && PathComparer.Instance.Compare(candidate, best) < 0) best = candidate; }
        foreach (var field in fields)
        {
            // Space-separated abbreviations must each be evidenced; do not concatenate them into
            // one synthetic initialism of an unrelated title. Whole-phrase text still stays intact.
            if (Match(query.Whole, field, versionId, query.Terms.Count <= 1) is not { } evidence) continue;
            int rank = Rank(evidence.Kind, evidence.Term.Field == SearchField.Title && evidence.Kind == 0, false);
            Consider(ToPath([evidence], rank, false));
        }
        if (query.Terms.Count > 1) Consider(Combine(query.Terms, fields, versionId, [], false, token));
        if (versionId.Length > 0 && query.Terms.Count > 1)
        {
            var terms = new List<SearchKey>();
            var versionEvidence = new List<Evidence>();
            bool valid = true;
            foreach (var term in query.Terms)
            {
                string? intent = VersionIntent(term.Literal);
                if (intent is null) terms.Add(term);
                else if (HasVersionEvidence(intent, versionName, original))
                    versionEvidence.Add(new(new(term.Literal, SearchField.Version, SearchMatchKind.Version, true, versionId), 0, 0));
                else { valid = false; break; }
            }
            if (valid && versionEvidence.Count > 0 && terms.Count > 0)
                Consider(Combine(terms, fields, versionId, versionEvidence, true, token));
        }
        return best;
    }
    private static string? VersionIntent(string token) => token switch
    {
        "现场版" or "現場版" or "LIVE" => "live",
        "伴奏版" or "伴奏" => "instrumental",
        "原版MV" or "原版" => "original",
        _ => null
    };
    private static bool HasVersionEvidence(string intent, string version, bool original)
    {
        string normalized = LocalSearchText.Fold(LocalSearchText.Normalize(version));
        return intent switch
        {
            "live" => normalized.Contains("现场", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(normalized, @"(?<![A-Z])LIVE(?![A-Z])"),
            "instrumental" => normalized.Contains("伴奏", StringComparison.Ordinal),
            "original" => original || normalized.Contains("原版MV", StringComparison.Ordinal) || normalized.Contains("原版 MV", StringComparison.Ordinal),
            _ => false
        };
    }
    private static Path? Combine(IReadOnlyList<SearchKey> terms, IReadOnlyList<Field> fields, string versionId,
        IReadOnlyList<Evidence> extra, bool versionIntent, CancellationToken token)
    {
        if (terms.Count == 0) return null;
        // Keep alternatives for field coverage and weakest necessary evidence. A greedy field choice loses
        // exact title+artist paths when the same word appears in two fields.
        var states = new Dictionary<(int Mask, int Worst), List<Evidence>> { [(0, 0)] = [] };
        foreach (var term in terms)
        {
            token.ThrowIfCancellationRequested();
            var evidence = fields.Select(field => Match(term, field, versionId)).Where(item => item is not null).Cast<Evidence>().ToArray();
            if (evidence.Length == 0) return null;
            var next = new Dictionary<(int Mask, int Worst), List<Evidence>>();
            foreach (var state in states)
                foreach (var item in evidence)
                {
                    var key = (state.Key.Mask | 1 << (int)item.Term.Field, Math.Max(state.Key.Worst, item.Kind));
                    List<Evidence> candidate = [.. state.Value, item];
                    if (!next.TryGetValue(key, out var previous) || PathComparer.Instance.Compare(ToPath(candidate, 0, false), ToPath(previous, 0, false)) < 0)
                        next[key] = candidate;
                }
            states = next;
        }
        Path? best = null;
        foreach (var state in states)
        {
            bool titleArtist = (state.Key.Mask & 3) == 3 && state.Key.Worst == 0;
            int rank = titleArtist ? 0 : Rank(state.Key.Worst, false, versionIntent);
            if (versionIntent) rank = Math.Max(2, rank);
            var path = ToPath([.. state.Value, .. extra], rank, versionIntent);
            if (PathComparer.Instance.Compare(path, best) < 0) best = path;
        }
        return best;
    }
    private static int Rank(int kind, bool wholeTitleExact, bool versionIntent) => kind switch
    {
        0 => wholeTitleExact && !versionIntent ? 1 : 2,
        1 => 3, 2 => 4, 3 or 4 => 5, _ => 6
    };
    private static Path ToPath(IReadOnlyList<Evidence> terms, int rank, bool versionIntent)
    {
        int weak = terms.Count > 0 ? terms.Max(item => item.Kind) : 0;
        return new(rank, weak is 4 or 6 ? 1 : 0, terms.Count(item => item.Kind == 0 && item.Term.Field != SearchField.Version),
            terms.Count(item => item.Term.Field == SearchField.Title), terms.Count(item => !item.Term.IsLiteral), terms.Sum(item => item.Extra),
            new(Array.AsReadOnly(terms.Select(item => item.Term).ToArray()), rank, versionIntent));
    }
    private static Evidence? Match(SearchKey term, Field field, string versionId, bool allowTransliteration = true)
    {
        Evidence? best = null;
        void Try(string query, string value, bool literal)
        {
            if (query.Length == 0 || value.Length == 0) return;
            int kind = value == query ? 0 : value.StartsWith(query, StringComparison.Ordinal) ? 1 : value.Contains(query, StringComparison.Ordinal) ? 2 : -1;
            if (kind < 0) return;
            var candidate = new Evidence(new(term.Literal, field.Kind, (SearchMatchKind)kind, literal, versionId), kind, Math.Max(0, value.Length - query.Length));
            if (best is null || candidate.Kind < best.Kind || candidate.Kind == best.Kind && (literal && !best.Term.IsLiteral || literal == best.Term.IsLiteral && candidate.Extra < best.Extra)) best = candidate;
        }
        Try(term.Literal, field.Key.Literal, true);
        Try(term.Folded, field.Key.Folded, false);
        Try(term.Compact, field.Key.Compact, false);
        if (allowTransliteration && LocalSearchText.IsLatinQuery(term.Compact))
        {
            if (field.Key.Pinyin.Length > 0)
            {
                int kind = field.Key.Pinyin == term.Compact ? 3 : field.Key.Pinyin.StartsWith(term.Compact, StringComparison.Ordinal) ? 4 : -1;
                if (kind >= 0 && (best is null || kind < best.Kind)) best = new(new(term.Literal, field.Kind, (SearchMatchKind)kind, false, versionId), kind, Math.Max(0, field.Key.Pinyin.Length - term.Compact.Length));
            }
            if (field.Key.Initials.Length > 0)
            {
                int kind = field.Key.Initials == term.Compact ? 5 : field.Key.Initials.StartsWith(term.Compact, StringComparison.Ordinal) ? 6 : -1;
                if (kind >= 0 && (best is null || kind < best.Kind)) best = new(new(term.Literal, field.Kind, (SearchMatchKind)kind, false, versionId), kind, Math.Max(0, field.Key.Initials.Length - term.Compact.Length));
            }
        }
        return best;
    }
}
