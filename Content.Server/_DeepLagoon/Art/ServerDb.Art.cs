using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<Guid> ArchiveArtworkAsync(ArchivedArtwork work);
    Task<List<Guid>> GetArtworkPoolAsync();
    Task<ArchivedArtwork?> GetArtworkAsync(Guid id);
}
public sealed partial class ServerDbManager
{
    public Task<Guid> ArchiveArtworkAsync(ArchivedArtwork work) => RunDbCommand(() => _db.ArchiveArtworkAsync(work));
    public Task<List<Guid>> GetArtworkPoolAsync() => RunDbCommand(() => _db.GetArtworkPoolAsync());
    public Task<ArchivedArtwork?> GetArtworkAsync(Guid id) => RunDbCommand(() => _db.GetArtworkAsync(id));
}
public abstract partial class ServerDbBase
{
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
