using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim backgroundVideoSlots = new(1, 1);

    private async Task<LibraryEnrichmentOutcome> SynchronizeBackgroundVideoAsync(LibraryEnrichmentTicket ticket, string sourceKey)
    {
        var token = ticket.CancellationToken;
        SongLookupInput? input = null;
        string? path = null;
        bool Current() => !closed && ticket.IsCurrent && viewModel.GetEnrichmentSourceKey(ticket.SongId) == sourceKey;
        await DispatchEnrichmentAsync(() =>
        {
            if (Current() && viewModel.GetLookupInput(ticket.SongId) is { } current
                && ticket.Key.StartsWith(sourceKey + "\n" + current.Key + "\n", StringComparison.Ordinal))
            {
                input = current;
                // Availability is already established by the source snapshot. Do not probe disk on UI.
                path = viewModel.FindSong(ticket.SongId)?.Song.Sources.FirstOrDefault();
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        if (input is null || path is null || !SongVideoLookupState.CanSearch(input, path)) return LibraryEnrichmentOutcome.Completed;
        string identity = SongVideoLookupState.Identity(input, sourceKey);
        if (input.Details.VideoLookup?.ShouldLookup(identity, DateTimeOffset.UtcNow) == false)
            return LibraryEnrichmentOutcome.Retry;

        await backgroundVideoSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Another lane/source revision may have retired this request while it waited for the slot.
            bool current = false;
            await DispatchEnrichmentAsync(() =>
            {
                current = Current() && viewModel.GetLookupInput(ticket.SongId) is { } latest
                    && SongVideoLookupState.Identity(latest, sourceKey) == identity
                    && latest.Details.Video is null;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            if (!current) return LibraryEnrichmentOutcome.Completed;

            var metadata = input.Details.Effective("");
            MusicVideoMatch? video = null;
            LookupOutcome outcome;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var candidates = await videoLookup.SearchAsync(metadata.Title, metadata.Artist, deadline.Token).ConfigureAwait(false);
                video = BilibiliVideoClient.ChooseReliable(candidates, metadata.Title, metadata.Artist,
                    metadata.DurationSeconds, input.FileName);
                outcome = video is null ? LookupOutcome.NotFound : LookupOutcome.Matched;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { outcome = LookupOutcome.Unavailable; }
            token.ThrowIfCancellationRequested();
            bool saved = false;
            await DispatchEnrichmentAsync(async () =>
            {
                saved = await viewModel.SaveBackgroundVideoAsync(ticket.SongId, sourceKey, input, video, outcome, Current);
            }).ConfigureAwait(false);
            if (!saved) return LibraryEnrichmentOutcome.Completed;
            return outcome == LookupOutcome.Matched ? LibraryEnrichmentOutcome.Completed : LibraryEnrichmentOutcome.Retry;
        }
        finally { backgroundVideoSlots.Release(); }
    }
}
