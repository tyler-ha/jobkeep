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
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        ...(init?.body ? { 'Content-Type': 'application/json' } : null),
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
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
};

/* ---- Shared domain types ------------------------------------------------ */

/* Enums serialize by NAME over REST — "Interviewing", not 2. This is the whole
 * set; there is no "Saved" and no "Screening", so the Pipeline board has
 * exactly five columns. */
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

export const listApplications = (query = '') =>
  api.get<ApplicationPage>(`/applications${query}`);
