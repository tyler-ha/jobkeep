import type {
  ApplicationDetail,
  ApplicationFunnel,
  ApplicationPage,
  MatchCheckResponse,
  CompanyRollupItem,
  ImportResponse,
  ImportSummary,
  ResumeDetail,
  ResumeSummary,
  SkillDemandItem,
} from '../lib/api';

/* Payloads shaped like the ones the C# records actually serialise.
 *
 * They are hand-written against src/Modules/**, not captured from a running
 * API, which is the point: the front end's recurring failure mode is guessing a
 * field name — `skillName` not `name`, `company` not `companyName`, dateApplied
 * as a DateOnly string and not a timestamp. A fixture that agrees with the C#
 * makes the screens fail here rather than in the browser.
 *
 * Nothing personal in them. The résumé fixture is deliberately a made-up
 * person: the real stored CV contains the author's own contact details, and
 * test data has a way of ending up in a screenshot.
 */

export const APP_ID = 'a4b172d5-5741-4e9b-8879-6fd59701968f';
export const RESUME_ID = 'c4d9af56-0000-4000-8000-000000000001';
export const IMPORT_ID = 'd1e2f3a4-0000-4000-8000-000000000002';

export const listPage: ApplicationPage = {
  items: [
    {
      id: APP_ID,
      company: 'REA Group',
      title: 'Senior Backend Engineer (.NET)',
      location: 'Richmond, Melbourne VIC',
      status: 'Applied',
      /* Deliberately old, so Today's "quiet for a while" block has something in
       * it and the cutoff comparison is actually exercised. */
      dateApplied: '2020-01-15',
      skills: ['C#', '.NET', 'PostgreSQL', 'AWS', 'Docker'],
      /* Phase 8. The default list can only ever hold live rows, so false is the
       * honest fixture — a test that wants an archived row sets it explicitly. */
      isArchived: false,
    },
    {
      id: 'a1f74664-0000-4000-8000-000000000003',
      company: 'Culture Amp',
      title: 'Backend Engineer',
      location: 'Melbourne VIC',
      status: 'Interviewing',
      dateApplied: '2026-08-20',
      skills: ['Go', 'Kubernetes'],
      isArchived: false,
    },
  ],
  totalCount: 2,
  page: 1,
  pageSize: 25,
  totalPages: 1,
};

export const detail: ApplicationDetail = {
  id: APP_ID,
  status: 'Applied',
  dateApplied: '2020-01-15',
  notes: null,
  resumeId: RESUME_ID,
  resumeLabel: 'demo-cv',
  createdAtUtc: '2026-08-29T04:27:00Z',
  updatedAtUtc: '2026-08-29T04:27:00Z',
  posting: {
    id: 'p0000000-0000-4000-8000-000000000004',
    title: 'Senior Backend Engineer (.NET)',
    location: 'Richmond, Melbourne VIC',
    employmentType: 'FullTime',
    salaryMin: 150000,
    salaryMax: 175000,
    salaryCurrency: 'AUD',
    salaryPeriod: 'Year',
    description: 'We are looking for a backend engineer.\n\nYou will own services end to end.',
    sourceUrl: 'https://example.com/job',
    postedDate: '2026-08-18',
    company: {
      id: 'c0000000-0000-4000-8000-000000000005',
      name: 'REA Group',
      website: null,
      industry: null,
      hqLocation: 'Melbourne',
    },
    skills: [
      { skillName: 'C#', category: 'Language', isRequired: true, source: 'Parsed' },
      { skillName: '.NET', category: 'Framework', isRequired: true, source: 'AiExtracted' },
      { skillName: 'Docker', category: null, isRequired: false, source: 'Parsed' },
    ],
    requirements: [
      {
        id: 'r0000000-0000-4000-8000-000000000006',
        text: 'Five years of commercial backend experience.',
        kind: 'Qualification',
        isMustHave: true,
      },
    ],
  },
};

export const funnel: ApplicationFunnel = {
  stages: [
    { status: 'Applied', count: 1 },
    { status: 'Interviewing', count: 1 },
    { status: 'Offer', count: 0 },
    { status: 'Rejected', count: 0 },
    { status: 'Withdrawn', count: 0 },
  ],
  total: 2,
};

export const skillDemand: SkillDemandItem[] = [
  { name: 'C#', category: 'Language', postingCount: 9 },
  { name: 'PostgreSQL', category: 'Database', postingCount: 4 },
  { name: 'Terraform', category: null, postingCount: 1 },
];

export const companies: CompanyRollupItem[] = [
  { name: 'REA Group', applicationCount: 1 },
  { name: 'Culture Amp', applicationCount: 1 },
];

export const resumes: ResumeSummary[] = [
  {
    id: RESUME_ID,
    label: 'demo-cv',
    fullName: 'Alex Demo',
    location: 'Melbourne VIC',
    sourceFormat: 'Pdf',
    skillCount: 3,
    createdAtUtc: '2026-08-20T01:00:00Z',
    updatedAtUtc: '2026-08-28T01:00:00Z',
    isArchived: false,
  },
];

export const resumeDetail: ResumeDetail = {
  id: RESUME_ID,
  label: 'demo-cv',
  fullName: 'Alex Demo',
  email: 'alex.demo@example.com',
  phone: null,
  location: 'Melbourne VIC',
  headline: 'Backend engineer',
  sourceText: 'Alex Demo — Backend engineer. Experienced with C#, SQL and containers.',
  sourceFileName: 'alex-demo-cv.pdf',
  sourceFormat: 'Pdf',
  createdAtUtc: '2026-08-20T01:00:00Z',
  updatedAtUtc: '2026-08-28T01:00:00Z',
  skills: [
    { skillName: 'C#', category: 'Language', source: 'Parsed' },
    { skillName: 'SQL', category: 'Database', source: 'AiExtracted' },
  ],
  experiences: [
    {
      id: 'e0000000-0000-4000-8000-000000000007',
      employer: 'Example Pty Ltd',
      title: 'Engineer',
      startText: 'Mar 2021',
      endText: 'present',
      highlights: ['Built an API.'],
      ordinal: 0,
    },
  ],
  educations: [
    {
      id: 'ed000000-0000-4000-8000-000000000008',
      institution: 'A University',
      qualification: 'BSc',
      yearText: '2019',
      ordinal: 0,
    },
  ],
};

export const matchCheck: MatchCheckResponse = {
  applicationId: APP_ID,
  resumeId: RESUME_ID,
  resumeLabel: 'demo-cv',
  matchedSkills: ['C#'],
  missingMustHaveSkills: ['.NET'],
  missingNiceToHaveSkills: ['Docker'],
  unmetRequirements: ['Five years of commercial backend experience.'],
  formattingRiskNotes: [],
  warning: null,
  checkedAtUtc: '2026-08-30T02:00:00Z',
};

export const imports: ImportSummary[] = [
  {
    id: IMPORT_ID,
    kind: 'Resume',
    status: 'AwaitingReview',
    fileName: 'alex-demo-cv.pdf',
    format: 'Pdf',
    byteCount: 48_000,
    textLength: 3200,
    warning: null,
    createdAtUtc: '2026-08-30T03:00:00Z',
    committedEntityId: null,
  },
];

export const importDetail: ImportResponse = {
  id: IMPORT_ID,
  kind: 'Resume',
  status: 'AwaitingReview',
  fileName: 'alex-demo-cv.pdf',
  format: 'Pdf',
  byteCount: 48_000,
  contentHash: 'abc123',
  extractedText: 'Alex Demo — Backend engineer. C#, SQL, containers.',
  draft: {
    resume: {
      label: 'alex-demo-cv',
      fullName: 'Alex Demo',
      email: 'alex.demo@example.com',
      phone: null,
      location: 'Melbourne VIC',
      headline: null,
      skills: ['C#', 'SQL'],
      // Phase 14. Non-empty on purpose: an empty list would let a review screen
      // that silently drops soft skills still pass this fixture.
      softSkills: ['Communication', 'Mentoring'],
      experience: [
        {
          employer: 'Example Pty Ltd',
          title: 'Engineer',
          start: 'Mar 2021',
          end: 'present',
          highlights: ['Built an API.'],
        },
      ],
      education: [{ institution: 'A University', qualification: 'BSc', year: '2019' }],
    },
    posting: null,
  },
  modelUsed: 'llama3.2:3b',
  warning: null,
  createdAtUtc: '2026-08-30T03:00:00Z',
  updatedAtUtc: '2026-08-30T03:00:00Z',
  committedEntityId: null,
};

/** A fetch stand-in that answers by path, the way the real API does. Anything
 *  it does not recognise throws by name rather than returning undefined — a
 *  screen quietly rendering an empty state because a URL was misspelled is
 *  exactly the failure these tests exist to catch. */
export function stubFetch() {
  return async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = new URL(String(input));
    const path = url.pathname;
    const method = init?.method ?? 'GET';

    const ok = (body: unknown) =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });

    if (path === '/applications' && method === 'GET') return ok(listPage);
    /* Phase 6.6 added a description box to the add form, so creating is now a
       path the screen tests walk. The body is echoed back inside the detail
       shape, which is enough for the caller and keeps the fixture honest about
       what the API returns (ApplicationDetail, not the request). */
    if (path === '/applications' && method === 'POST') return ok(detail);
    if (path === '/stats/funnel') return ok(funnel);
    if (path === '/stats/skill-demand') return ok(skillDemand);
    if (path === '/stats/companies') return ok(companies);
    if (path === '/resumes') return ok(resumes);
    if (path === `/resumes/${RESUME_ID}`) return ok(resumeDetail);
    if (path === '/imports' && method === 'GET') return ok(imports);
    /* The two ways in, Phase 6.5. Both answer with the created import, because
       both really do: /imports/text delegates to the upload's own handler, so a
       fixture that answered them differently would be lying about the one
       property the backend tests exist to pin. */
    if ((path === '/imports' || path === '/imports/text') && method === 'POST')
      return ok({ ...importDetail, status: 'Parsing' });
    if (path === `/imports/${IMPORT_ID}`) return ok(importDetail);
    if (path === `/applications/${APP_ID}/match-check`) return ok(matchCheck);
    /* 404 is a real answer here, not a failure: GetAnalysis returns it for "not
     * analysed yet" and the Job post screen renders that as an invitation. */
    if (path === `/applications/${APP_ID}/analysis`) return new Response(null, { status: 404 });
    /* AnalyzePosting.cs refuses with a 400 when the posting has no description.
       That is a rule, not a fault, and the Job post screen must explain it in
       place — before Phase 6.6 it went to setError, which replaced the entire
       screen with a failure card. */
    if (path === `/applications/${APP_ID}/analyze` && method === 'POST')
      return new Response(
        JSON.stringify({ detail: 'This posting has no description to analyze. Add one first.' }),
        { status: 400, headers: { 'Content-Type': 'application/json' } },
      );
    /* Phase 8. DELETE archives and POST /restore undoes it; both answer 204 with
       no body, which is what the real routes do (ToHttpResult(_ => NoContent)).
       Neither is given a fixture that changes `listPage`, deliberately — these
       tests are about what the screen DOES when the call succeeds, and a stub
       that also simulated the server's filtering would be testing the stub. */
    if (path === `/applications/${APP_ID}` && method === 'DELETE')
      return new Response(null, { status: 204 });
    if (path === `/applications/${APP_ID}/restore` && method === 'POST')
      return new Response(null, { status: 204 });

    if (path === `/applications/${APP_ID}`) return ok(detail);

    throw new Error(`No fixture for ${method} ${path}`);
  };
}
