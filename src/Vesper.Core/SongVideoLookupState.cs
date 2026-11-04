using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Core;

/// <summary>Durable negative/retry state for conservative background MV association.</summary>
public sealed record SongVideoLookupState(string Key, DateTimeOffset CheckedAt, LookupOutcome Outcome)
{
    public bool ShouldLookup(string key, DateTimeOffset now) => key != Key
        || Outcome != LookupOutcome.Matched && now - CheckedAt >= (Outcome == LookupOutcome.Unavailable
            ? TimeSpan.FromMinutes(1) : TimeSpan.FromDays(1));

    public static string Identity(SongLookupInput input, string sourceKey)
    {
        var metadata = input.Details.Effective("");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Version = 1, Source = sourceKey, Input = input.Key, metadata.Title, metadata.Artist, metadata.DurationSeconds
        }))));
    }

    public static bool CanSearch(SongLookupInput input, string path)
    {
        if (input.Details.Video is not null || string.IsNullOrWhiteSpace(path)) return false;
        string extension = Path.GetExtension(path);
        if (!new[] { ".mp3", ".m4a", ".wav", ".flac" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
        var metadata = input.Details.Effective("");
        return metadata.Title.Length > 0 && metadata.Artist.Length > 0
            && double.IsFinite(metadata.DurationSeconds) && metadata.DurationSeconds > 0;
    }
}
