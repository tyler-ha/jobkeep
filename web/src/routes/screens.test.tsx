import { render, screen, within } from '@testing-library/react';
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

  it('ATS check derives its five stages from the stored result', async () => {
    at(`/applications/${APP_ID}/ats-check`);
    expect(await screen.findByRole('heading', { name: 'ATS check' })).toBeTruthy();
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

  it('Import shows the review queue', async () => {
    at('/import');
    expect(await screen.findByText('alex-demo-cv.pdf')).toBeTruthy();
  });

  it('Import review puts the draft beside the extracted text', async () => {
    at(`/import/${IMPORT_ID}`);
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

  it('an unknown address says so rather than rendering nothing', async () => {
    at('/nonsense');
    expect(await screen.findByRole('heading', { name: 'No screen at this address' })).toBeTruthy();
  });
});
