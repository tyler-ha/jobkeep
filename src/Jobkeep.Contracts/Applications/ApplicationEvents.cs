namespace Jobkeep.Modules.Applications;

// PHASE 13.3c: the two events that replace the two CASCADEs 13.3b dropped. The
// namespace is deliberately Jobkeep.Modules.Applications -- 13.6 renames
// namespaces to match projects, in one pass, once nothing else is moving.
//
// ---------------------------------------------------------------------------
// Why these live in Contracts rather than in Applications
// ---------------------------------------------------------------------------
// An event is read by its subscribers and written by its publisher, so it is
// exactly the shared vocabulary this project already puts here: the same reason
// ApplicationRef and ResumeRef are here rather than beside the tables they
// describe. When Ats becomes a service, this record is the message body on the
// wire, and Contracts is the project the wire schema is generated from.
//
// ---------------------------------------------------------------------------
// Why they carry an id and nothing else
// ---------------------------------------------------------------------------
// The temptation is to put the deleted row's fields in the message so the
// subscriber does not have to ask for them. That is right for an event about a
// row that still EXISTS and wrong here: after a delete there is nothing left to
// ask about, so a subscriber that needed more than the id would be relying on
// this message being complete forever. Both subscribers below delete their own
// row by foreign key, which is all an id supports and all a cascade ever did.
//
// Past tense, and it matters: this is a statement that something HAS happened,
// not a request to do something. The publisher has already committed by the time
// it is raised (DeleteApplication.cs argues the ordering), so a subscriber
// cannot veto it. That distinction is what makes these events rather than
// commands, and it is why the résumé delete-side checks — which genuinely CAN
// refuse — are contract calls instead (DeleteResume.cs).

// An application row is gone. Ats subscribes: `ats_results.ApplicationId` was a
// CASCADE until 13.3b, and an ATS check means nothing without the application it
// judged.
public record ApplicationDeleted(Guid ApplicationId);

// A job posting row is gone. Ai subscribes: `ai_analyses.PostingId` was a CASCADE
// until 13.3b, and an analysis is a description of an ad that no longer exists.
public record PostingDeleted(Guid PostingId);
