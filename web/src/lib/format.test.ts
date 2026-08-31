import { describe, expect, it } from 'vitest';

import { formatDateOnly, formatSalary, humanise, isoDaysAgo } from './format';

/* These are the front end's pure functions, and they exist because two of them
 * encode a bug that has already been paid for once.
 *
 * The backend suite cannot reach any of this: it asserts what the API sends,
 * and every one of these failures happens after that. A DateOnly rendered a day
 * early is a correct response displayed wrongly.
 */

describe('formatDateOnly', () => {
  /* THE test. "2026-03-01" is a calendar day, not an instant. `new Date()`
   * parses the bare form as UTC midnight, and Melbourne is UTC+10/+11, so
   * rendering that in local time gives 29 February — a day early, and in this
   * case a day that mostly does not exist. If this ever fails, someone has
   * replaced the string surgery in format.ts with a Date. */
  it('does not shift the day, whatever the local timezone is', () => {
    expect(formatDateOnly('2026-03-01')).toBe('1 Mar');
    expect(formatDateOnly('2026-01-01')).toBe('1 Jan');
    expect(formatDateOnly('2026-12-31')).toBe('31 Dec');
  });

  it('drops the leading zero on the day', () => {
    expect(formatDateOnly('2026-08-09')).toBe('9 Aug');
  });

  /* The year is noise on a screen of this year's applications, and load-bearing
   * on last year's. The current year is read at call time, so this test asks
   * for a year that cannot be the current one rather than hard-coding 2026. */
  it('shows the year only when it is not the current one', () => {
    const thisYear = new Date().getFullYear();
    expect(formatDateOnly(`${thisYear}-08-29`)).toBe('29 Aug');
    expect(formatDateOnly(`${thisYear - 1}-08-29`)).toBe(`29 Aug ${thisYear - 1}`);
  });

  /* Better a raw value on screen than a crash or the word "undefined". */
  it('passes anything unparseable straight through', () => {
    expect(formatDateOnly('not a date')).toBe('not a date');
    expect(formatDateOnly('2026-13-01')).toBe('2026-13-01');
  });
});

describe('formatSalary', () => {
  it('renders a range with an en dash, not a hyphen', () => {
    expect(formatSalary(150000, 175000, 'AUD', 'Year')).toBe('$150–175k');
  });

  it('marks an open-ended range in the direction it is open', () => {
    expect(formatSalary(150000, null, 'AUD', 'Year')).toBe('$150k+');
    expect(formatSalary(null, 175000, 'AUD', 'Year')).toBe('$175k max');
  });

  it('names the period unless it is the assumed one', () => {
    expect(formatSalary(85, 95, 'AUD', 'Hour')).toBe('$85–95 / hour');
    expect(formatSalary(85, 95, 'AUD', 'Year')).toBe('$85–95');
  });

  /* An ad that did not say gets nothing rendered, not "$0" and not "—": the
   * caller decides how to show an absence, which is why this returns null. */
  it('returns null when the ad named no figure', () => {
    expect(formatSalary(null, null, 'AUD', 'Year')).toBeNull();
  });

  it('spells out a currency it has no symbol for', () => {
    expect(formatSalary(90000, null, 'EUR', 'Year')).toBe('EUR 90k+');
  });
});

describe('humanise', () => {
  /* Enum names arrive PascalCase over REST — "FullTime", not 2. */
  it('splits PascalCase into a sentence', () => {
    expect(humanise('FullTime')).toBe('Full time');
    expect(humanise('AiExtracted')).toBe('Ai extracted');
  });

  it('leaves a single word alone bar its case', () => {
    expect(humanise('Contract')).toBe('Contract');
  });
});

describe('isoDaysAgo', () => {
  it('returns a zero-padded ISO calendar day', () => {
    expect(isoDaysAgo(0)).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  /* The property the callers rely on: ISO dates sort lexicographically, so
   * "older than n days" is a string comparison and never a parsed one. */
  it('goes further back the larger n is, as a string comparison', () => {
    expect(isoDaysAgo(30) < isoDaysAgo(7)).toBe(true);
    expect(isoDaysAgo(7) < isoDaysAgo(0)).toBe(true);
  });

  /* Fixed days, because these are exactly the cases that are wrong for a few
   * days a year and right the rest of the time. */
  it('rolls back over a month boundary by calendar length', () => {
    const mar1 = new Date(2026, 2, 1);
    expect(isoDaysAgo(1, mar1)).toBe('2026-02-28');
    expect(isoDaysAgo(29, mar1)).toBe('2026-01-31');
  });

  it('handles a leap day and a year boundary', () => {
    expect(isoDaysAgo(1, new Date(2024, 2, 1))).toBe('2024-02-29');
    expect(isoDaysAgo(1, new Date(2026, 0, 1))).toBe('2025-12-31');
  });

  it('defaults to today', () => {
    const now = new Date();
    const today = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(
      now.getDate(),
    ).padStart(2, '0')}`;
    expect(isoDaysAgo(0)).toBe(today);
  });
});
