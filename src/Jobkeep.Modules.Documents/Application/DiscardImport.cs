using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: throw a draft away without committing it.
//
// The row is marked Discarded rather than deleted, and that is a deliberate
// choice with one concrete payoff: the extracted text stays readable. When an
// import produces nonsense, the diagnostic question is always "was the PDF
// extracted badly, or was the text structured badly", and only the stored text
// answers it. Deleting the row deletes the evidence at exactly the moment
// somebody wants it.
//
// The cost is a table that accumulates rejected imports. At single-user volume
// that is a handful of rows; if it ever matters, a purge is a delete statement,
// and it would then be a deliberate retention decision rather than an accident
// of the delete button. The security audit's retention item (APP 11.2) is where
// that decision belongs, because a discarded resume is still a resume.
public class DiscardImportHandler
{
    private readonly IDocumentsDbContext _db;

    public DiscardImportHandler(IDocumentsDbContext db) => _db = db;

    public async Task<SliceResult<bool>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var import = await _db.DocumentImports.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (import is null)
            return SliceResult<bool>.NotFound($"Import {id} not found.");

        if (import.Status == ImportStatus.Committed)
            return SliceResult<bool>.Invalid(
                "This import has already been committed. Delete the resume or application it created instead.");

        // Discarding an already-discarded import is a no-op, not an error: the
        // caller is asking for a state that already holds. Same reasoning as
        // AddSkillToPosting's duplicate link.
        if (import.Status == ImportStatus.Discarded)
            return SliceResult<bool>.Ok(true);

        import.Status = ImportStatus.Discarded;
        import.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return SliceResult<bool>.Ok(true);
    }
}
