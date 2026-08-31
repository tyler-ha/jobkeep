import { describe, expect, it } from 'vitest';

import { barScale, shares } from './chart';

describe('shares', () => {
  /* The reason this function exists. Naive rounding gives 33+33+17+8+8 = 99 and
   * the stacked bar ends 1% short of its container, which reads as a rendering
   * bug rather than as arithmetic. */
  it('always sums to exactly 100', () => {
    for (const counts of [
      [4, 4, 2, 1, 1],
      [1, 1, 1],
      [7],
      [1, 2, 3, 4, 5, 6, 7],
      [100, 1],
      [1, 1, 1, 1, 1, 1],
    ]) {
      expect(shares(counts).reduce((a, b) => a + b, 0)).toBe(100);
    }
  });

  it('gives the leftover units to the segments that lost the most', () => {
    /* Thirds: 33.33 each, floors to 33+33+33 = 99, one unit to hand back. All
     * three lost the same, so it goes to the first — stably, not by whatever
     * order the sort happened to leave them in. */
    expect(shares([1, 1, 1])).toEqual([34, 33, 33]);
  });

  it('is exact when the numbers already divide evenly', () => {
    expect(shares([1, 1, 1, 1])).toEqual([25, 25, 25, 25]);
    expect(shares([3, 1])).toEqual([75, 25]);
  });

  /* An empty funnel is the first thing a new user sees, and dividing by zero
   * here would put NaN into a width. */
  it('returns zeros rather than dividing by zero', () => {
    expect(shares([0, 0, 0])).toEqual([0, 0, 0]);
    expect(shares([])).toEqual([]);
  });
});

describe('barScale', () => {
  it('scales to the largest value, not to the sum', () => {
    expect(barScale([10, 5, 1])).toEqual([100, 50, 10]);
  });

  /* One mention next to forty is real. Rounded honestly it is a 3% bar, which
   * at any usable panel width is a sliver indistinguishable from nothing — so
   * the chart would be showing "none" for something that is not none. */
  it('keeps a small but real value visible', () => {
    expect(barScale([40, 1])).toEqual([100, 4]);
    expect(barScale([40, 1], 8)).toEqual([100, 8]);
  });

  it('leaves a genuine zero at zero', () => {
    expect(barScale([10, 0])).toEqual([100, 0]);
    expect(barScale([0, 0])).toEqual([0, 0]);
    expect(barScale([])).toEqual([]);
  });
});
