import { describe, expect, it } from 'vitest';

import { PARSE_MEDIAN_MS, estimateProgress } from './progress';

describe('estimateProgress', () => {
  it('starts at zero', () => {
    expect(estimateProgress(0)).toBe(0);
  });

  /* Defensive rather than hypothetical: `Date.now() - startedAt` can come back
   * as 0 or, on a clock adjustment, negative. Either would otherwise produce a
   * bar that jumps backwards or renders NaN%. */
  it('treats a negative or nonsensical elapsed time as the start', () => {
    expect(estimateProgress(-500)).toBe(0);
    expect(estimateProgress(Number.NaN)).toBe(0);
    expect(estimateProgress(5_000, 0)).toBe(0);
  });

  it('only ever moves forward', () => {
    let last = -1;
    for (let ms = 0; ms <= 180_000; ms += 250) {
      const now = estimateProgress(ms);
      expect(now).toBeGreaterThanOrEqual(last);
      last = now;
    }
  });

  /* The property the whole design rests on. Only the response sets 100%; a bar
   * that arrives at the end on its own and then sits there for another minute
   * has told the user the app is broken. `ModelOptions.TimeoutSeconds` is 180,
   * so 10x the median is inside the range this actually has to survive. */
  it('never reaches 1, even at ten times the median', () => {
    expect(estimateProgress(PARSE_MEDIAN_MS * 10)).toBeLessThan(1);
    expect(estimateProgress(180_000)).toBeLessThan(1);
  });

  /* And it never parks. A bar stalled on a round number is the thing everyone
   * has learned to distrust, so the late stretch must still be moving. */
  it('is still moving late in the wait', () => {
    expect(estimateProgress(120_000)).toBeGreaterThan(estimateProgress(90_000));
  });

  it('reads about three quarters at the median', () => {
    const atMedian = estimateProgress(PARSE_MEDIAN_MS);
    expect(atMedian).toBeGreaterThan(0.7);
    expect(atMedian).toBeLessThan(0.8);
  });
});
