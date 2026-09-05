namespace Jobkeep.SharedKernel;

// Phase 11.2b — who the current request belongs to, for the five module
// contexts and the audit interceptor.
//
// SCOPED, and the setter is not an accident. Two callers write it:
//
//   * The composition root, from the request principal's NameIdentifier claim.
//     That is the normal path and it is read-only in spirit.
//   * ImportParseWorker, which runs OUTSIDE any request. Its scope has no
//     principal, so without this it would see an empty database — it sweeps
//     every `Parsing` row at startup, and every one of them belongs to someone.
//     It sets the owner it read from the row and then sends the ordinary slice,
//     so the background path and the HTTP path run the same code with the same
//     filter, rather than the worker getting an IgnoreQueryFilters exemption
//     that would then be one grep away from being copied somewhere it is wrong.
//
// Null means "nobody", and a null owner matches no row rather than every row —
// `OwnerUserId == null` is NULL in SQL. That is the safe direction, and it is
// why this is a `Guid?` and not a `Guid` with a sentinel.
public interface ICurrentUser
{
    Guid? UserId { get; set; }
}
