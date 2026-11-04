using Vesper.Core;

namespace Vesper.App;

public sealed partial class MainViewModel
{
    private long catalogRevision = -1;
    private void ReconcileCatalog()
    {
        if (catalogRevision == requestedSearchIndexRevision && Library.Catalog is not null) return;
        Library.Catalog ??= new();
        Library.Catalog.Reconcile(Library);
        catalogRevision = requestedSearchIndexRevision;
    }
    // Readers receive detached catalog values, never the mutable persistence model.
    public LibraryCatalog GetCatalogSnapshot()
    {
        lock (searchIndexGate)
        {
            ReconcileCatalog();
            return Library.Catalog.Snapshot();
        }
    }
}
