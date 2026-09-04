/* The fetch core. Every call to the backend goes through here.
 *
 * No dev-server proxy on purpose. Phase 6.1 added a real, named CORS policy to
 * the API precisely so the browser makes a genuine cross-origin request in
 * development — the same shape it will make in production. A Vite proxy would
 * hide that behind a same-origin illusion and the first deploy would be the
 * first time CORS was ever exercised.
 */

const BASE = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5080';

/** Thrown for any non-2xx. Carries the status so callers can branch on it. */
export class ApiError extends Error {
  readonly status: number;
  readonly detail?: string;

  constructor(status: number, message: string, detail?: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.detail = detail;
  }

  /* A 400 from the API is a rule the domain enforced, not a fault. The status
   * lifecycle is the live case: dragging a card to Offer from a closed
   * application is refused, and the Pipeline board has to render that as a
   * normal, explained outcome rather than an error state. */
  get isRuleRefusal() {
    return this.status === 400;
  }

  /* A 404 is not always a failure either. `GET /applications/{id}/match-check`
   * answers 404 for "never checked" as well as for "no such application" —
   * GetMatchResult.cs says so in a comment and declines to spend a second query
   * distinguishing them. The Job post screen reads that as an invitation. */
  get isMissing() {
    return this.status === 404;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        /* FormData sets its own Content-Type, and it has to: the header carries
         * the multipart boundary the browser generated for that exact body.
         * Naming application/json here — or naming multipart/form-data without
         * a boundary — makes the upload fail at model binding, which presents
         * as a 400 with no useful detail. */
        ...(init?.body && !(init.body instanceof FormData)
          ? { 'Content-Type': 'application/json' }
          : null),
        ...init?.headers,
      },
    });
  } catch {
    /* fetch only rejects for network-level failures — the API being down, or
     * CORS refusing the response. Both look identical from here, and both mean
     * the same thing to the user. */
    throw new ApiError(0, 'Could not reach the API. Is it running on ' + BASE + '?');
  }

  if (!res.ok) {
    /* ASP.NET returns ProblemDetails for handled failures. Fall back to the
     * status text when the body is empty or not JSON. */
    let detail: string | undefined;
    try {
      const body = await res.json();
      detail = body?.detail ?? body?.title ?? undefined;
    } catch {
      /* no body, or not JSON — the status alone is what we have */
    }
    throw new ApiError(res.status, detail ?? res.statusText ?? 'Request failed', detail);
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }),
  /* PATCH, never PUT. `PUT /applications/{id}` is a 405 — this has cost a
   * debugging cycle before and is worth the comment. */
  patch: <T>(path: string, body: unknown) =>
    request<T>(path, { method: 'PATCH', body: JSON.stringify(body) }),
  /* PUT is a full replace, and the import review is the only thing that wants
   * one — see reviewImport below. Applications are PATCHed. */
  put: <T>(path: string, body: unknown) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body) }),
  upload: <T>(path: string, form: FormData) =>
    request<T>(path, { method: 'POST', body: form }),
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
};

/** Normalises anything thrown by a fetch chain into an ApiError, so a screen's
 *  error state only ever has one shape to render. */
export const asApiError = (e: unknown) =>
  e instanceof ApiError ? e : new ApiError(0, String(e));

/* ---- Enums -------------------------------------------------------------- */

/* All of these serialize by NAME over REST — "Interviewing", not 2. They are
 * string unions rather than TS enums so the wire value and the type are the
 * same thing and there is nothing to convert. Source: src/Models/Enums.cs. */

/* This is the whole set; there is no "Saved" and no "Screening", so the
 * Pipeline board has exactly five columns. */
export const APPLICATION_STATUSES = [
  'Applied',
  'Interviewing',
  'Offer',
  'Rejected',
  'Withdrawn',
] as const;

export type ApplicationStatus = (typeof APPLICATION_STATUSES)[number];

/* Rejected and Withdrawn are closed, not terminal — a user may move an
 * application back out of them, because Huntr and Teal both allow it. The one
 * surviving invariant is that Offer is only reachable from an active
 * application, and the API is the authority on that, not this constant. */
export const CLOSED_STATUSES: readonly ApplicationStatus[] = ['Rejected', 'Withdrawn'];

export type EmploymentType = 'FullTime' | 'PartTime' | 'Contract' | 'Casual' | 'Internship';
export type SalaryPeriod = 'Hour' | 'Day' | 'Month' | 'Year';
export type RequirementKind = 'Qualification' | 'Responsibility' | 'Benefit';
export type SeniorityLevel = 'Unknown' | 'Junior' | 'Mid' | 'Senior' | 'Lead' | 'Principal';
export type SourceFormat = 'PlainText' | 'Markdown' | 'Pdf' | 'Docx';

/* Which rows a model wrote. The Job post screen marks them, because a user
 * correcting the ad's skill list needs to know what was extracted rather than
 * parsed — the check reads these rows, not the prose. */
export type SkillSource = 'Parsed' | 'AiExtracted';

/* Phase 14. Is this a capability or a way of working? Set on the skill row when
 * it is first created — by the seeded vocabulary, or by whichever import named
 * it first — and never overwritten afterwards.
 *
 * Only on the DRAFT types for now, deliberately. The stored skill responses
 * (PostingSkillResponse, ResumeSkillItem) do not carry it, because nothing
 * renders it yet and an unread field on the wire is schema nobody can safely
 * remove later. Adding it there is additive when a screen wants it. */
export type SkillKind = 'Unknown' | 'Technical' | 'Soft';

/* ---- Applications ------------------------------------------------------- */

/* Mirrors ApplicationListItem in src/Modules/Applications/ListApplications.cs.
 * Deliberately flat and deliberately not the detail shape — description,
 * résumé text, salary and requirements are detail-view fields. */
export interface ApplicationListItem {
  id: string;
  company: string;
  title: string;
  location: string | null;
  status: ApplicationStatus;
  /** DateOnly over the wire — "2026-08-29". Not a timestamp; do not new Date() it
   *  and then format in local time, or dates near midnight shift by a day. */
  dateApplied: string;
  skills: string[];
  /** Phase 8. Only ever true on a page fetched with `?includeArchived=true` —
   *  the default list cannot contain an archived row, so a screen that never
   *  asks for them can ignore this field entirely. It is sent rather than
   *  inferred from the request because a component re-rendering off cached data
   *  no longer knows which request produced it. */
  isArchived: boolean;
}

/** The list is paged. A concrete page type rather than a generic, because
 *  HotChocolate names GraphQL types after the CLR type. */
export interface ApplicationPage {
  items: ApplicationListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

/* Mirrors BoardCard in src/Jobkeep.Modules.Applications/Application/GetBoard.cs.
 * Phase 9, gap 3 — narrower than a list row on purpose: the board's card shows
 * how MANY skills, never which, so the server sends a count and skips the
 * catalog lookup the list has to make. */
export interface BoardCard {
  id: string;
  company: string;
  title: string;
  status: ApplicationStatus;
  /** DateOnly, like the list's. */
  dateApplied: string;
  skillCount: number;
}

/** Not paged — a board is truncated or complete, because page two of a board is
 *  not somewhere you can drag a card. `totalCount` is the count before the cap,
 *  so the screen can say how many are missing. */
export interface ApplicationBoard {
  cards: BoardCard[];
  totalCount: number;
}

/* Mirrors ApplicationQuery in ListApplications.cs. Phase 9 made `status` a SET
 * (a repeated query parameter over REST) and added `isClosed` as the domain's
 * own name for Rejected-or-Withdrawn — which is why the Applications screen has
 * a Closed tab and does not spell that set out here. */
export const APPLICATION_SORTS = ['DateApplied', 'Company', 'Title', 'Status', 'UpdatedAt'] as const;
export type ApplicationSort = (typeof APPLICATION_SORTS)[number];

/* Mirrors CompanyResponse / PostingResponse / ApplicationDetail in
 * src/Modules/Applications/ApplicationDetail.cs. */
export interface CompanyResponse {
  id: string;
  name: string;
  website: string | null;
  industry: string | null;
  hqLocation: string | null;
}

/** AddSkillToPosting.cs. Note the field is `skillName`, not `name`. */
export interface PostingSkillResponse {
  skillName: string;
  category: string | null;
  isRequired: boolean;
  source: SkillSource;
}

/** AddRequirementToPosting.cs. */
export interface RequirementResponse {
  id: string;
  text: string;
  kind: RequirementKind;
  isMustHave: boolean;
}

export interface PostingResponse {
  id: string;
  title: string;
  location: string | null;
  employmentType: EmploymentType;
  salaryMin: number | null;
  salaryMax: number | null;
  salaryCurrency: string;
  salaryPeriod: SalaryPeriod;
  description: string | null;
  sourceUrl: string | null;
  /** DateOnly again — same no-parsing rule as dateApplied. */
  postedDate: string | null;
  company: CompanyResponse;
  skills: PostingSkillResponse[];
  requirements: RequirementResponse[];
}

export interface ApplicationDetail {
  id: string;
  status: ApplicationStatus;
  dateApplied: string;
  notes: string | null;
  /* Phase 4.5: the résumé is referenced, not inlined. The label rides along so
   * a client can render "tyler-cv-2025" without a second round trip. */
  resumeId: string | null;
  resumeLabel: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  posting: PostingResponse;
}

/** CreateApplication.cs. Company is a name, not an id — the API does the
 *  find-or-create (CompanyLookup.cs). */
export interface CreateApplicationRequest {
  company: string;
  title: string;
  location?: string | null;
  description?: string | null;
  sourceUrl?: string | null;
  notes?: string | null;
  resumeId?: string | null;
}

/** UpdateApplication.cs. Every field optional — this is a PATCH. */
export interface UpdateApplicationRequest {
  company?: string | null;
  title?: string | null;
  location?: string | null;
  status?: ApplicationStatus | null;
  notes?: string | null;
  description?: string | null;
  resumeId?: string | null;
}

/* ---- Ai ----------------------------------------------------------------- */

/** GetAnalysis.cs. The Ai module owns ai_analyses, so this is a second request
 *  rather than a field on the application detail — a module boundary, not an
 *  oversight (ApplicationDetail.cs says so at length). */
export interface AnalysisSummaryResponse {
  postingId: string;
  seniority: SeniorityLevel;
  summary: string | null;
  modelUsed: string | null;
  analyzedAtUtc: string;
}

/* ---- Match ---------------------------------------------------------------- */

/** RunMatchCheck.cs. Returned by both POST (run it) and GET (read the stored one),
 *  deliberately the same DTO so the two routes cannot drift apart.
 *
 *  There is no score, and that is a decision, not a gap: the real-CV test found
 *  the biggest ATS risk was the parser losing the candidate's name, which a
 *  number out of 100 would have averaged away into a digit. */
export interface MatchCheckResponse {
  applicationId: string;
  resumeId: string | null;
  resumeLabel: string | null;
  matchedSkills: string[];
  missingMustHaveSkills: string[];
  missingNiceToHaveSkills: string[];
  unmetRequirements: string[];
  formattingRiskNotes: string[];
  /** Set when the model could not be reached, so stage 3 did not run. Stored,
   *  not computed — an unstored warning would let a later read of an empty
   *  unmetRequirements claim every requirement was met. Always render it. */
  warning: string | null;
  checkedAtUtc: string;
}

/* ---- Documents ---------------------------------------------------------- */

/** ListResumes.cs. Deliberately omits sourceText; the detail carries it. */
export interface ResumeSummary {
  id: string;
  label: string;
  fullName: string | null;
  location: string | null;
  sourceFormat: SourceFormat | null;
  skillCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  /** Phase 8, same contract as ApplicationListItem.isArchived. */
  isArchived: boolean;
}

/** GetResume.cs. `skillName`, not `name`. */
export interface ResumeSkillItem {
  skillName: string;
  category: string | null;
  source: SkillSource;
}

export interface ResumeExperienceItem {
  id: string;
  employer: string;
  title: string | null;
  startText: string | null;
  endText: string | null;
  highlights: string[];
  ordinal: number;
}

export interface ResumeEducationItem {
  id: string;
  institution: string | null;
  qualification: string | null;
  yearText: string | null;
  ordinal: number;
}

/** GetResume.cs. `sourceText` is the verbatim extracted text — it is what the
 *  parser actually read, and it is real personal data. It stays on screen and
 *  never goes anywhere else. */
export interface ResumeDetail {
  id: string;
  label: string;
  fullName: string | null;
  email: string | null;
  phone: string | null;
  location: string | null;
  headline: string | null;
  sourceText: string;
  sourceFileName: string | null;
  sourceFormat: SourceFormat | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  skills: ResumeSkillItem[];
  experiences: ResumeExperienceItem[];
  educations: ResumeEducationItem[];
}

/* ---- Documents: imports ------------------------------------------------- */

/* The import cycle is the one place in the app where nothing a model produced
 * reaches a real table until a human has said yes. These types mirror
 * src/Modules/Documents/ — ImportDraft.cs for the draft, ImportDocument.cs for
 * the response, ListImports.cs for the queue. */

export type DocumentKind = 'Resume' | 'JobPosting';

/** ImportStatus. `Committed` is terminal — a committed import is a receipt.
 *  Discarded rows are kept rather than deleted so a bad parse stays diagnosable.
 *
 *  `CommitFailed` arrived with backend Phase 13.2c, when confirming a job ad
 *  stopped being one database transaction: the application is created by another
 *  module, so a commit can now stop half-way. It means "this was attempted and
 *  did not finish, confirm it again" — the server knows whether a retry starts
 *  over or only closes the import out, so the screen does not have to.
 *
 *  It is EDITABLE, like AwaitingReview, and for the same reason: the user's next
 *  move is to fix something and press the button again.
 *
 *  `Parsing` arrived with Phase 6.5 group 6, when the upload stopped blocking on
 *  the model. `POST /imports` returns as soon as the text is extracted and saved,
 *  and a background worker on the server structures it afterwards. So this status
 *  is the one the review screen WATCHES: seeing it means "poll until it changes",
 *  and the transition away from it is the completion event.
 *
 *  An intermediate version had the client drive the model itself through
 *  `/reparse`. It was replaced because a browser tab then owned the work and
 *  closing it stranded the row. The server owns it now, including rows left
 *  behind by a crash — so a Parsing row always eventually resolves.
 *
 *  It is NOT editable and NOT confirmable; the draft does not exist yet, and both
 *  endpoints refuse it with a "still being read" message rather than the
 *  "already"/"no longer" wording every terminal state uses. */
export type ImportStatus =
  | 'AwaitingReview'
  | 'Parsing'
  | 'Committed'
  | 'Discarded'
  | 'CommitFailed';

/** ListImports.cs. Deliberately WITHOUT the extracted text or the draft — a
 *  résumé is personal information and a list endpoint is the wrong place to
 *  spray it. `textLength` is computed in SQL so the text is never read off
 *  disk for this query. */
export interface ImportSummary {
  id: string;
  kind: DocumentKind;
  status: ImportStatus;
  fileName: string;
  format: SourceFormat;
  byteCount: number;
  textLength: number;
  warning: string | null;
  createdAtUtc: string;
  committedEntityId: string | null;
}

export interface DraftExperience {
  employer: string;
  title: string | null;
  /* Dates stay strings, carried through unparsed. Transcribing "Mar 2021"
   * beats guessing a DateOnly — Models/Resume.cs makes the argument. */
  start: string | null;
  end: string | null;
  highlights: string[];
}

export interface DraftEducation {
  institution: string;
  qualification: string | null;
  year: string | null;
}

export interface ResumeDraft {
  /** Not proposed by the model: it is a decision about how YOU organise your
   *  résumés, and the document contains no evidence about it. Seeded from the
   *  filename and changed here at review. */
  label: string;
  fullName: string | null;
  email: string | null;
  phone: string | null;
  location: string | null;
  headline: string | null;
  skills: string[];
  /** Phase 14. The résumé's soft skills, as a second list rather than a tag on
   *  each entry in `skills` — ImportDraft.cs says why. Both lists become
   *  resume_skills rows at commit; the split only carries the kind. */
  softSkills: string[];
  experience: DraftExperience[];
  education: DraftEducation[];
}

export interface DraftPostingSkill {
  name: string;
  required: boolean;
  kind: SkillKind;
}

export interface DraftRequirement {
  text: string;
  kind: RequirementKind;
  isMustHave: boolean;
}

export interface PostingDraft {
  company: string;
  title: string;
  location: string | null;
  description: string | null;
  sourceUrl: string | null;
  skills: DraftPostingSkill[];
  requirements: DraftRequirement[];
}

/** Both halves nullable, exactly one populated, decided by the import's Kind.
 *  ImportDraft.cs explains why this is not a discriminated union: it has to
 *  cross a GraphQL schema, where a union forces every client into
 *  `... on ResumeDraft` fragments for a discriminator they already know. */
export interface ImportDraft {
  resume: ResumeDraft | null;
  posting: PostingDraft | null;
}

/** ImportDocument.cs. Carries the extracted text, and that is the exception
 *  that proves the over-fetch rule: the user's job on the review screen is to
 *  decide whether the draft matches the document, which cannot be done without
 *  the document in front of them. */
export interface ImportResponse {
  id: string;
  kind: DocumentKind;
  status: ImportStatus;
  fileName: string;
  format: SourceFormat;
  byteCount: number;
  contentHash: string;
  extractedText: string;
  draft: ImportDraft;
  modelUsed: string | null;
  warning: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  committedEntityId: string | null;
}

/** CommitImport.cs. The receipt: what actually came into existence. */
export interface CommitResponse {
  importId: string;
  kind: DocumentKind;
  committedEntityId: string;
  description: string;
  skillsLinked: number;
  experiencesCreated: number;
  educationsCreated: number;
  requirementsCreated: number;
}

/* ---- Call sites --------------------------------------------------------- */

/* Thin named wrappers, not a service layer: each is one line naming one route,
 * so the path and its verb live next to the type they return instead of being
 * spelled out as a string literal in three screens. A screen still owns its own
 * use case — it composes these, and nothing composes them for it. */

export const listApplications = (query = '') =>
  api.get<ApplicationPage>(`/applications${query}`);

/* Phase 9, gap 3. One request for the whole board, where this used to be a loop
 * over up to five pages of the list. */
export const getApplicationBoard = () => api.get<ApplicationBoard>('/applications/board');

export const getApplication = (id: string) =>
  api.get<ApplicationDetail>(`/applications/${id}`);

export const createApplication = (body: CreateApplicationRequest) =>
  api.post<ApplicationDetail>('/applications', body);

export const updateApplication = (id: string, body: UpdateApplicationRequest) =>
  api.patch<ApplicationDetail>(`/applications/${id}`, body);

/* Phase 8 — DELETE archives; it does not destroy. The route and the verb are
 * unchanged and that is deliberate (DeleteApplication.cs argues it): from the
 * client's side the row stops being returned by every read, which is what DELETE
 * has always meant here. The name is `archiveApplication` on THIS side only,
 * because the UI says "Archive" and a screen calling `deleteApplication` beside
 * an Undo button reads like a bug. */
export const archiveApplication = (id: string) => api.delete<void>(`/applications/${id}`);

/** The undo. 404 if the application is live or absent — restoring addresses a row
 *  in the archive, and a live one is not in it. */
export const restoreApplication = (id: string) =>
  api.post<void>(`/applications/${id}/restore`, {});

export const addPostingSkill = (
  id: string,
  body: { skillName: string; category?: string | null; isRequired: boolean },
) => api.post<PostingSkillResponse>(`/applications/${id}/skills`, body);

export const removePostingSkill = (id: string, skillName: string) =>
  api.delete<void>(`/applications/${id}/skills/${encodeURIComponent(skillName)}`);

export const addRequirement = (
  id: string,
  body: { text: string; kind: RequirementKind; isMustHave: boolean },
) => api.post<RequirementResponse>(`/applications/${id}/requirements`, body);

export const removeRequirement = (id: string, requirementId: string) =>
  api.delete<void>(`/applications/${id}/requirements/${requirementId}`);

export const analyzePosting = (id: string) =>
  api.post<unknown>(`/applications/${id}/analyze`);

export const getAnalysis = (id: string) =>
  api.get<AnalysisSummaryResponse>(`/applications/${id}/analysis`);

/** POST runs the check and overwrites the stored row — match_results is 1:1 with
 *  the application and latest wins. `resumeId` overrides the linked résumé so
 *  the same job can be checked against a second CV without editing it. */
export const runMatchCheck = (id: string, resumeId?: string) =>
  api.post<MatchCheckResponse>(
    `/applications/${id}/match-check${resumeId ? `?resumeId=${resumeId}` : ''}`,
  );

/** GET reads the stored result without recomputing. 404 means "never checked". */
export const getMatchResult = (id: string) =>
  api.get<MatchCheckResponse>(`/applications/${id}/match-check`);

export const listResumes = (includeArchived = false) =>
  api.get<ResumeSummary[]>(`/resumes${includeArchived ? '?includeArchived=true' : ''}`);

export const getResume = (id: string) => api.get<ResumeDetail>(`/resumes/${id}`);

export const addResumeSkill = (id: string, skillName: string, category?: string | null) =>
  api.post<ResumeSkillItem>(`/resumes/${id}/skills`, { skillName, category: category ?? null });

/** The inverse, added in 6.1. Without it a skill dragged on by mistake could
 *  not be taken off, and the approved design ships the drag in both directions. */
export const removeResumeSkill = (id: string, skillName: string) =>
  api.delete<void>(`/resumes/${id}/skills/${encodeURIComponent(skillName)}`);

/* ---- Imports ------------------------------------------------------------ */

/* THE SCREEN IS CALLED UPLOAD; EVERYTHING BELOW IS STILL CALLED IMPORT, AND
 * THAT SPLIT IS DELIBERATE (Phase 6.5).
 *
 * The route renamed to /upload because the screen was calling itself three
 * different things at once — "Import" in the nav, "Upload a CV or a job ad" in
 * the lede, "Upload a document" on the panel. The wire did not follow it. These
 * names — ImportResponse, ImportStatus, ImportDraft, uploadImport,
 * confirmImport — mirror the backend records they deserialize (`/imports`,
 * `DocumentImport`, `ImportStatus`), and a type that lies about the shape it
 * came off the network as is worse than one that disagrees with a nav label.
 *
 * So: if you are renaming these, you are renaming the API too, and that is a
 * different change. If you are adding a route, it goes under /imports.
 */

/** The queue: what is still waiting on you. Passing a status widens it; there
 *  is no "everything" option, because the committed rows are receipts and the
 *  interesting view of one is the row it created. Not paged — ListImports.cs
 *  argues that a review queue long enough to need paging has already failed. */
export const listImports = (status?: ImportStatus) =>
  api.get<ImportSummary[]>(`/imports${status ? `?status=${status}` : ''}`);

export const getImport = (id: string) => api.get<ImportResponse>(`/imports/${id}`);

/** Multipart, and the ONLY thing on the API that is REST-only — uploading has
 *  no GraphQL equivalent on purpose (DocumentsModule.cs argues why at length).
 *  No Content-Type header: the browser has to set it itself so the multipart
 *  boundary matches the body it generated. */
export const uploadImport = (file: File, kind: DocumentKind, label?: string, sourceUrl?: string) => {
  const form = new FormData();
  form.append('file', file);
  form.append('kind', kind);
  if (label) form.append('label', label);
  if (sourceUrl) form.append('sourceUrl', sourceUrl);
  return api.upload<ImportResponse>('/imports', form);
};

/** The paste route — the same import with a string in place of a file. A
 *  SIBLING endpoint rather than `file` becoming optional on the one above:
 *  ImportText.cs argues why, and the short version is that one route with two
 *  mutually exclusive bodies is how the Swagger document got taken down once
 *  already. JSON, so the default Content-Type applies and there is no multipart
 *  boundary to get out of the browser's way. */
export const importText = (
  text: string,
  kind: DocumentKind,
  label?: string,
  sourceUrl?: string,
) => api.post<ImportResponse>('/imports/text', { text, kind, label, sourceUrl });

/** PUT, not PATCH, and a full replace of the draft — ReviewImport.cs makes the
 *  case: a partial update of a nested draft cannot express "delete the third
 *  experience", which is the correction a bad parse most often needs. */
export const reviewImport = (id: string, draft: ImportDraft) =>
  api.put<ImportResponse>(`/imports/${id}`, draft);

/** Run the model over the stored text again. POST because it is neither safe
 *  nor idempotent: it costs a model call and overwrites the draft. */
export const reparseImport = (id: string) => api.post<ImportResponse>(`/imports/${id}/reparse`);

/** The gate. Everything before this writes one row in one table nothing else
 *  reads; this is where a résumé, an application, skills and requirements come
 *  into existence. */
export const confirmImport = (id: string) => api.post<CommitResponse>(`/imports/${id}/confirm`);

/** Discard. Marks the row rather than removing it, so a bad parse stays
 *  diagnosable — the extracted text survives, which is how you tell "the PDF
 *  extracted badly" from "the model structured it badly". */
export const discardImport = (id: string) => api.delete<void>(`/imports/${id}`);

/* ---- Analytics ---------------------------------------------------------- */

/** StatusFunnel.cs. One GROUP BY, aggregated in SQL. The Applications screen
 *  uses it for the per-status tab counts: the list endpoint filters by one
 *  status at a time, so counting the others client-side would need five more
 *  requests to get what one aggregate already answers. */
export interface StatusCount {
  status: ApplicationStatus;
  count: number;
}

export interface ApplicationFunnel {
  stages: StatusCount[];
  total: number;
}

export const getFunnel = () => api.get<ApplicationFunnel>('/stats/funnel');

/** SkillDemand.cs. One GROUP BY over the skills every ad you recorded names.
 *
 *  The gap this comment used to record is FIXED, in two halves. Phase 7 put a
 *  unique index on lower("Name"), so `C#` and `c#` are one row; Phase 14 added
 *  skill_aliases, so `Agile` and `Agile Methodologies` are too. The chart still
 *  merges nothing client-side — it does not have to, because the rows arrive
 *  already merged. */
export interface SkillDemandItem {
  name: string;
  category: string | null;
  postingCount: number;
}

/** CompanyRollup.cs. */
export interface CompanyRollupItem {
  name: string;
  applicationCount: number;
}

export const getSkillDemand = (top = 12) =>
  api.get<SkillDemandItem[]>(`/stats/skill-demand?top=${top}`);

export const getCompanies = (top = 12) =>
  api.get<CompanyRollupItem[]>(`/stats/companies?top=${top}`);
