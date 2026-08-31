/* Formatting for the two kinds of time the API returns, which are not the same
 * kind and must not be treated as one.
 *
 * `DateOnly` (dateApplied, postedDate) arrives as "2026-08-29" and means a
 * calendar day. `DateTime` (checkedAtUtc, analyzedAtUtc) arrives as an instant.
 * Running a calendar day through `new Date()` parses it as UTC midnight and
 * then renders it in local time, which in Melbourne moves it to the previous
 * day for ten hours out of every twenty-four. So the DateOnly path never
 * constructs a Date at all.
 */

const MONTHS = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

/** "2026-08-29" → "29 Aug". String surgery, deliberately — see the note above. */
export function formatDateOnly(value: string): string {
  const [y, m, d] = value.split('-');
  const month = MONTHS[Number(m) - 1];
  if (!month || !d) return value;
  const day = String(Number(d));
  /* The year only appears when it is not the current one. On a screen that is
   * mostly this year's applications, printing "2026" on every row is noise. */
  return Number(y) === new Date().getFullYear() ? `${day} ${month}` : `${day} ${month} ${y}`;
}

/** An instant, so this one does localise. "28 Aug, 4:27pm". */
export function formatInstant(value: string): string {
  const at = new Date(value);
  if (Number.isNaN(at.getTime())) return value;
  const time = at
    .toLocaleTimeString('en-AU', { hour: 'numeric', minute: '2-digit' })
    .replace(/\s/g, '')
    .toLowerCase();
  return `${at.getDate()} ${MONTHS[at.getMonth()]}, ${time}`;
}

/** "$150–175k + super" from the four salary columns, or null when the ad did
 *  not say. An en dash, not a hyphen, and a thin space before the unit. */
export function formatSalary(
  min: number | null,
  max: number | null,
  currency: string,
  period: string,
): string | null {
  if (min == null && max == null) return null;
  const symbol = currency === 'AUD' || currency === 'USD' ? '$' : `${currency} `;
  const short = (n: number) => (n >= 1000 ? `${Math.round(n / 1000)}k` : String(n));
  const per = period === 'Year' ? '' : ` / ${period.toLowerCase()}`;
  if (min != null && max != null) {
    /* "$150–175k", not "$150k–175k". The unit is carried once by the pair when
     * both ends share it — which is how every ad in the category writes it, and
     * what this file's own header promises. A mixed range ("$900–1k") keeps both
     * units, because there the second one is not a repeat of the first. */
    const sameUnit = min >= 1000 === max >= 1000;
    const low = sameUnit ? short(min).replace(/k$/, '') : short(min);
    return `${symbol}${low}–${short(max)}${per}`;
  }
  return `${symbol}${short((min ?? max) as number)}${per}${min != null ? '+' : ' max'}`;
}

/** Enum names arrive in PascalCase. "FullTime" → "Full time". */
export function humanise(value: string): string {
  const spaced = value.replace(/([a-z])([A-Z])/g, '$1 $2');
  return spaced.charAt(0) + spaced.slice(1).toLowerCase();
}

/** The calendar day `n` days before today, as "2026-08-10".
 *
 *  This exists so a "has this been sitting a while" test can be a plain string
 *  comparison: ISO dates sort lexicographically, so `dateApplied <= isoDaysAgo(21)`
 *  is exact, needs no parsing, and cannot drift by a day the way comparing two
 *  Dates across a timezone boundary does. The Date built here is only ever used
 *  for its own calendar arithmetic and never for a value that is displayed.
 *
 *  `from` is injectable so the month- and year-rollover cases can be tested for
 *  a fixed day. A test that asks for "365 days ago" and asserts a year boundary
 *  passes for 364 days of the year and fails on the 31st of December in a leap
 *  year, which is the worst possible time to find out. */
export function isoDaysAgo(n: number, from: Date = new Date()): string {
  const d = new Date(from.getFullYear(), from.getMonth(), from.getDate() - n);
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  return `${d.getFullYear()}-${mm}-${dd}`;
}
