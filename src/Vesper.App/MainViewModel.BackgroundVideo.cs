using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainViewModel
{
    public Task<bool> SaveBackgroundVideoAsync(string id, string sourceKey, SongLookupInput expected,
        MusicVideoMatch? video, LookupOutcome outcome, Func<bool> canPublish)
    {
        lock (searchIndexGate)
        {
            if (!initialized || disposed || ReadOnly || !canPublish() || GetEnrichmentSourceKey(id) != sourceKey
                || GetLookupInput(id) is not { } currentInput || currentInput.Key != expected.Key
                || SongVideoLookupState.Identity(currentInput, sourceKey) != SongVideoLookupState.Identity(expected, sourceKey))
                return Task.FromResult(false);
            var current = Library.Details.GetValueOrDefault(id) ?? new();
            // An explicit selection or another accepted match wins over a late automatic result.
            if (current.Video is not null || current.Video != expected.Details.Video
                || (video is not null) != (outcome == LookupOutcome.Matched)) return Task.FromResult(false);
            var updated = current with
            {
                Video = video,
                VideoLookup = new(SongVideoLookupState.Identity(currentInput, sourceKey), clock.GetUtcNow(), outcome)
            };
            Library.Details[id] = updated;
            backgroundMetadataRevision++;
            QueueBackgroundMetadataWrite();
            FindSong(id)?.ApplyDetails(updated);
            // MV associations do not change the local search projection or row ordering.
        }
        return Task.FromResult(true);
    }
}
