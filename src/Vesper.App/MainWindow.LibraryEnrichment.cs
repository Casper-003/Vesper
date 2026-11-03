using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private LibraryEnrichmentController? libraryEnrichment;

    private void StartLibraryEnrichment()
    {
        if (closed || libraryEnrichment is not null || viewModel.ReadOnly) return;
        libraryEnrichment = new(viewModel, ReadBackgroundLocalMetadataAsync, LookupBackgroundMetadataAsync, DispatchEnrichmentAsync);
        libraryEnrichment.StateChanged += OnLibraryEnrichmentStateChanged;
        libraryEnrichment.Start();
    }

    private Task DispatchEnrichmentAsync(Func<Task> action)
    {
        if (closed) return Task.FromCanceled(new CancellationToken(true));
        if (DispatcherQueue.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (closed) completion.TrySetCanceled();
                else { await action(); completion.TrySetResult(); }
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }

    private async Task<LibraryMetadataRead?> ReadBackgroundLocalMetadataAsync(Song song, CancellationToken token)
    {
        // Share the bounded disk-read slots with visible rows; never require a row container.
        await metadataSlots.WaitAsync(token);
        try
        {
            var read = await Task.Run(() => ReadFileMetadataAsync(song, token), token);
            token.ThrowIfCancellationRequested();
            if (read.Path is null) return null;
            if (read.Metadata is not null) return new(read.Path, read.Metadata, read.Resolution);
            // Some media have no Windows property handler. A readable file can
            // still be identified from its filename; do not trap it in the local lane.
            try
            {
                await using var probe = new FileStream(read.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                await probe.ReadAtLeastAsync(new byte[1], 1, throwOnEndOfStream: false, cancellationToken: token);
                return new(read.Path, new SongMetadata(), read.Resolution);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        finally { metadataSlots.Release(); }
    }

    private async Task<SongLookupResult> LookupBackgroundMetadataAsync(SongLookupInput input, CancellationToken token)
    {
        // A slow online song cannot hold up the independent local-label lane.
        await automaticLookupSlots.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try { return await AutomaticLookup.LookupAsync(input, deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return new(null, null, LookupOutcome.Unavailable, LookupOutcome.Unavailable); }
        }
        finally { automaticLookupSlots.Release(); }
    }

    private void OnLibraryEnrichmentStateChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed || libraryEnrichment is null) return;
        var state = libraryEnrichment.State;
        viewModel.SetEnrichmentActivity(state.IsBusy, state.Label, state.Tooltip);
    });

    private void CloseLibraryEnrichment()
    {
        if (libraryEnrichment is null) return;
        libraryEnrichment.StateChanged -= OnLibraryEnrichmentStateChanged;
        libraryEnrichment.Dispose();
        libraryEnrichment = null;
        viewModel.SetEnrichmentActivity(false, "", "");
    }
}
