using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<Guid> ArchiveArtworkAsync(ArchivedArtwork work);
    Task<List<Guid>> GetArtworkPoolAsync();
    Task<ArchivedArtwork?> GetArtworkAsync(Guid id);
    Task<List<ArchivedArtwork>> ListArtworksAsync(string search, int page);
    Task<bool> DeleteArtworkAsync(Guid id);
}
public sealed partial class ServerDbManager
{
    public Task<Guid> ArchiveArtworkAsync(ArchivedArtwork work) => RunDbCommand(() => _db.ArchiveArtworkAsync(work));
    public Task<List<Guid>> GetArtworkPoolAsync() => RunDbCommand(() => _db.GetArtworkPoolAsync());
    public Task<ArchivedArtwork?> GetArtworkAsync(Guid id) => RunDbCommand(() => _db.GetArtworkAsync(id));
    public Task<List<ArchivedArtwork>> ListArtworksAsync(string search, int page) => RunDbCommand(() => _db.ListArtworksAsync(search, page));
    public Task<bool> DeleteArtworkAsync(Guid id) => RunDbCommand(() => _db.DeleteArtworkAsync(id));
}
public abstract partial class ServerDbBase
{
    public async Task<List<ArchivedArtwork>> ListArtworksAsync(string search, int page)
    {
        await using var db = await GetDb();
        search = search.Trim();
        var hasAuthor = Guid.TryParse(search, out var author);
        var query = db.DbContext.ArchivedArtworks.AsNoTracking().Where(a => !a.Hidden)
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new ArchivedArtwork { Id = a.Id, Title = a.Title, AuthorName = a.AuthorName,
                Author = a.Author, CreatedAt = a.CreatedAt });
        var skip = Math.Clamp(page, 0, 100000) * Content.Shared._DeepLagoon.Art.ArtModerationEuiState.PageSize;
        var take = Content.Shared._DeepLagoon.Art.ArtModerationEuiState.PageSize + 1;
        if (search.Length == 0) return await query.Skip(skip).Take(take).ToListAsync();
        // SQLite LOWER/NOCASE do not fold Cyrillic. Stream metadata only for consistent Unicode search on both providers.
        var result = new List<ArchivedArtwork>();
        await foreach (var row in query.AsAsyncEnumerable())
        {
            if (!row.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !row.AuthorName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !(hasAuthor && row.Author == author)) continue;
            if (skip-- > 0) continue;
            result.Add(row);
            if (result.Count == take) break;
        }
        return result;
    }

    public async Task<bool> DeleteArtworkAsync(Guid id)
    {
        await using var db = await GetDb();
        // Keep the deduplication key as a tombstone so reframing cannot restore removed content.
        return await db.DbContext.ArchivedArtworks.Where(a => a.Id == id && !a.Hidden)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Hidden, true)
                .SetProperty(a => a.Document, "").SetProperty(a => a.Preview, "")) > 0;
    }

    public async Task<Guid> ArchiveArtworkAsync(ArchivedArtwork work)
    {
        await using var db = await GetDb();
        var existing = await db.DbContext.ArchivedArtworks.AsNoTracking()
            .FirstOrDefaultAsync(a => a.WorkId == work.WorkId && a.RevisionHash == work.RevisionHash);
        if (existing != null) return existing.Id;
        db.DbContext.ArchivedArtworks.Add(work);
        try { await db.DbContext.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Another frame may have archived the same restored canvas concurrently.
            db.DbContext.Entry(work).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            existing = await db.DbContext.ArchivedArtworks.AsNoTracking()
                .FirstOrDefaultAsync(a => a.WorkId == work.WorkId && a.RevisionHash == work.RevisionHash);
            if (existing == null) throw;
            return existing.Id;
        }
        return work.Id;
    }
    public async Task<List<Guid>> GetArtworkPoolAsync()
    {
        await using var db = await GetDb();
        return await db.DbContext.ArchivedArtworks.AsNoTracking().Where(a => !a.Hidden).Select(a => a.Id).ToListAsync();
    }
    public async Task<ArchivedArtwork?> GetArtworkAsync(Guid id)
    {
        await using var db = await GetDb();
        return await db.DbContext.ArchivedArtworks.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && !a.Hidden);
    }
}
