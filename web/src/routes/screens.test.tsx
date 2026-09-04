import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import App from '../App';
import { APP_ID, IMPORT_ID, RESUME_ID, stubFetch } from '../test/fixtures';

/* Every screen, rendered once against payloads shaped like the real ones.
 *
 * These are not a substitute for looking at the app — they say nothing about
 * whether it is any good to look at. They pin the class of failure that is
 * invisible until a screen is opened and then obvious: a field name guessed
 * wrong, a null the API is allowed to send, a hook order that only breaks on
 * the second render. Three of these screens shipped unopened; this is the floor
 * under that.
 *
 * The whole App is mounted rather than each route component, so the routing
 * table is exercised too — a screen that builds but is not reachable is still
 * broken.
 */

function at(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(stubFetch()));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('every screen renders', () => {
  it('Today shows the attention count and the status strip', async () => {
    at('/today');
    expect(await screen.findByRole('navigation', { name: 'Applications by status' })).toBeTruthy();
    /* The fixture's first application was applied in 2020 and is still Applied,
     * so the quiet block must have found it — which is the ISO-string date
     * comparison actually running. */
    expect(await screen.findByText(/Quiet for a while/)).toBeTruthy();
  });

  it('Applications lists rows and counts the tabs from the funnel', async () => {
    at('/applications');
    expect(await screen.findByText('Senior Backend Engineer (.NET)')).toBeTruthy();
    expect(await screen.findByRole('group', { name: 'Filter by status' })).toBeTruthy();
  });

  it('Applications honours a status deep link from Today', async () => {
    at('/applications?status=Interviewing');
    const tab = await screen.findByRole('button', { name: /Interviewing/ });
    expect(tab.getAttribute('aria-pressed')).toBe('true');
  });

  /* A status that is not one of the five must not be forwarded to the API,
   * which answers 400 for it. */
  it('Applications ignores a status it does not recognise', async () => {
    at('/applications?status=Screening');
    const tab = await screen.findByRole('button', { name: /^All/ });
    expect(tab.getAttribute('aria-pressed')).toBe('true');
  });

  it('Pipeline builds five columns', async () => {
    at('/pipeline');
    for (const status of ['Applied', 'Interviewing', 'Offer', 'Rejected', 'Withdrawn']) {
      expect(await screen.findByRole('region', { name: status })).toBeTruthy();
    }
  });

  it('Job post renders the ad and its skills', async () => {
    at(`/applications/${APP_ID}`);
    expect(await screen.findByRole('heading', { name: 'Senior Backend Engineer (.NET)' })).toBeTruthy();
    /* Salary formatting through the real formatter: the range carries the unit
     * once, not on both ends. */
    expect(await screen.findByText(/\$150–175k/)).toBeTruthy();
  });

  it('match check derives its five stages from the stored result', async () => {
    at(`/applications/${APP_ID}/match-check`);
    expect(await screen.findByRole('heading', { name: 'Match check' })).toBeTruthy();
    for (const stage of [
      'Contact details',
      'Must-have skills',
      'Nice to have',
      'Written requirements',
      'Format risks',
    ]) {
      expect(await screen.findByText(stage)).toBeTruthy();
    }
    /* The résumé picker is populated from GET /resumes, and the board defaults
     * to the one the application is linked to. */
    expect(await screen.findByRole('option', { name: 'demo-cv' })).toBeTruthy();
  });

  it('Résumés selects the first version and shows what the parser read', async () => {
    at(`/resumes/${RESUME_ID}`);
    expect(await screen.findByRole('heading', { name: 'demo-cv' })).toBeTruthy();
    expect(await screen.findByText('alex.demo@example.com')).toBeTruthy();
  });

  it('Upload shows the review queue', async () => {
    at('/upload');
    expect(await screen.findByText('alex-demo-cv.pdf')).toBeTruthy();
  });

  it('Upload review puts the draft beside the extracted text', async () => {
    at(`/upload/${IMPORT_ID}`);
    expect(await screen.findByRole('heading', { name: 'Is this your CV?' })).toBeTruthy();
    expect(await screen.findByRole('heading', { name: 'What was extracted' })).toBeTruthy();
  });

  it('Insights charts the three aggregates', async () => {
    at('/insights');
    expect(await screen.findByRole('heading', { name: 'What the ads ask for' })).toBeTruthy();
    /* shares() must sum to 100 across the funnel's segments — the legend prints
     * them, so a rounding regression shows up here rather than as a 1% gap at
     * the end of the bar. Scoped to the funnel panel: the company table prints
     * its own shares, which are a different set summing to 100 separately. */
    const panel = await screen.findByRole('region', { name: 'Where they sit' });
    const percents = within(panel)
      .getAllByText(/^\d+%$/)
      .map((n) => Number(n.textContent!.replace('%', '')));
    expect(percents).toHaveLength(5);
    expect(percents.reduce((a, b) => a + b, 0)).toBe(100);
  });

  /* The uploader had no test at all until Phase 6.5, which is how it kept a
   * `required` attribute that would have made the form unsubmittable the moment
   * the input was visually hidden. These two pin the only behaviour on it that
   * is a decision rather than markup. */
  it('Upload names the version after the file, until you say otherwise', async () => {
    const user = userEvent.setup();
    at('/upload');
    await screen.findByRole('heading', { name: 'Upload a document' });

    const label = screen.getByRole('textbox', { name: 'Call this version' });
    expect((label as HTMLInputElement).value).toBe('');

    /* The extension goes, because that is exactly what the server's own
       fallback does (ImportDocument.cs). Showing a different default from the
       one that would actually be stored is worse than showing none. */
    await user.upload(
      screen.getByLabelText(/Drop a file here/i),
      new File(['a cv'], 'tyler-cv-2025.pdf', { type: 'application/pdf' }),
    );
    expect((label as HTMLInputElement).value).toBe('tyler-cv-2025');

    /* And typing wins for good: swapping the file afterwards must not overwrite
       a name the user chose. This is the whole reason `labelTouched` exists. */
    await user.clear(label);
    await user.type(label, 'backend-focused');
    await user.upload(
      screen.getByLabelText(/tyler-cv-2025\.pdf/i),
      new File(['another'], 'generalist.docx', { type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' }),
    );
    expect((label as HTMLInputElement).value).toBe('backend-focused');
  });

  it('Upload will not submit until a file is chosen', async () => {
    at('/upload');
    const submit = await screen.findByRole('button', { name: /Upload and read/ });
    /* Disabled, and NOT `required` on the input. The input is visually hidden
       inside the drop zone, and Chrome refuses to submit a form holding an
       invalid control it cannot scroll to — silently, in the console. */
    expect((submit as HTMLButtonElement).disabled).toBe(true);
  });

  /* Phase 6.5 group 4. Some ads are never a file: the user selects the page,
     copies, and has nothing to upload. This walks that whole path, because the
     interesting part is not the textarea — it is that a short paste keeps the
     button disabled and a real one sends the ad to /imports/text intact. */
  it('Upload takes a pasted ad, and holds the button until it is long enough', async () => {
    const user = userEvent.setup();
    const fetchSpy = vi.fn(stubFetch());
    vi.stubGlobal('fetch', fetchSpy);

    at('/upload');
    await user.click(await screen.findByRole('radio', { name: /Paste text/ }));

    const box = screen.getByRole('textbox', { name: /Paste the advertisement/ });
    const submit = screen.getByRole('button', { name: /Read this text/ });

    /* Under the server's 40-character floor the button stays down, and the
       counter says by how much — a disabled button with no explanation is the
       failure this counter exists to prevent. */
    await user.type(box, 'Backend dev');
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/of 40 characters/)).toBeTruthy();

    const ad =
      'Senior Backend Engineer at Atlassian. We use C#, PostgreSQL and Kubernetes on AWS.';
    await user.clear(box);
    await user.type(box, ad);
    expect((submit as HTMLButtonElement).disabled).toBe(false);

    await user.click(submit);

    /* The ad reaches the paste route, whole. Asserted on the request body
       rather than on a rendered string: what matters is that no keyword was
       dropped between the textarea and the wire. */
    const posted = fetchSpy.mock.calls.find(
      ([url, init]) =>
        String(url).endsWith('/imports/text') && (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(posted).toBeTruthy();
    const body = JSON.parse((posted![1] as RequestInit).body as string);
    expect(body.kind).toBe('Resume');
    for (const keyword of ['Atlassian', 'C#', 'PostgreSQL', 'Kubernetes', 'AWS'])
      expect(body.text).toContain(keyword);
  });

  /* The three below are Phase 6.6. They exist because the app shipped a screen
   * that told the reader to "paste the ad in" and gave them nowhere to do it,
   * while the only textarea on the add form was wired to a field nothing reads.
   * None of that is visible to a renders-without-throwing test, which is why
   * these assert the wiring rather than the markup. */
  it('the add form offers the ad, and sends it as the description', async () => {
    const user = userEvent.setup();
    at('/applications');
    await user.click(await screen.findByRole('button', { name: /Add application/ }));

    await user.type(screen.getByRole('textbox', { name: 'Company' }), 'Airwallex');
    await user.type(screen.getByRole('textbox', { name: 'Role' }), 'Backend Engineer');
    await user.type(screen.getByRole('textbox', { name: /^The ad/ }), 'Kubernetes and Spring Boot.');
    /* Notes must NOT collect this. It is a different field on a different
       table, and the analyser has never read it. */
    await user.type(screen.getByRole('textbox', { name: /^Your notes/ }), 'referred by a friend');
    await user.click(screen.getByRole('button', { name: 'Save application' }));

    const post = (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls.find(
      ([, init]) => init?.method === 'POST',
    );
    expect(post).toBeTruthy();
    const body = JSON.parse(post![1].body as string);
    expect(body.description).toBe('Kubernetes and Spring Boot.');
    expect(body.notes).toBe('referred by a friend');
  });

  it('the job post can be given an ad, and PATCHes it as the description', async () => {
    const user = userEvent.setup();
    at(`/applications/${APP_ID}`);
    await user.click(await screen.findByRole('button', { name: /Edit the ad/ }));

    const box = screen.getByRole('textbox', { name: 'The advertisement text' });
    /* Prefilled from what is stored, so editing is editing and not retyping. */
    expect((box as HTMLTextAreaElement).value).toContain('backend engineer');

    await user.clear(box);
    await user.type(box, 'Go, Kubernetes, Grafana.');
    await user.click(screen.getByRole('button', { name: 'Save the ad' }));

    const patch = (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls.find(
      ([, init]) => init?.method === 'PATCH',
    );
    expect(patch).toBeTruthy();
    expect(JSON.parse(patch![1].body as string).description).toBe('Go, Kubernetes, Grafana.');
  });

  it('an analyser refusal is explained in place, not as a dead screen', async () => {
    const user = userEvent.setup();
    at(`/applications/${APP_ID}`);
    await user.click(await screen.findByRole('button', { name: /Analyse the ad/ }));

    expect(await screen.findByText(/nothing for it to read|no description to analyze/i)).toBeTruthy();
    /* The screen survives. Before this, a 400 went to setError and the whole
       detail view was replaced by a failure card — the heading below is what
       proves the difference. */
    expect(screen.getByRole('heading', { name: 'The ad' })).toBeTruthy();
  });

  it('an unknown address says so rather than rendering nothing', async () => {
    at('/nonsense');
    expect(await screen.findByRole('heading', { name: 'No screen at this address' })).toBeTruthy();
  });
});
