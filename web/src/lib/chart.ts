/* The arithmetic behind the three charts on Insights.
 *
 * It lives here rather than inside the screen for two reasons: a component file
 * that exports helpers trips `react/only-export-components`, and — the real
 * one — this is the part of a chart that can be wrong without looking wrong.
 * A bar chart with a subtly bad scale still renders as a bar chart.
 *
 * No charting library. Three charts do not earn a dependency, and the ones
 * worth having would want an SVG layout engine to draw what a `<div>` with a
 * width already draws correctly and accessibly.
 */

/** Percentages of the total, rounded so they sum to exactly 100.
 *
 *  Naive rounding does not: five statuses at 1/3, 1/3, 1/6, 1/12, 1/12 round to
 *  33 + 33 + 17 + 8 + 8 = 99, and a stacked bar built from those leaves a 1%
 *  gap at the end that reads as a rendering bug. Largest remainder fixes it —
 *  the units lost to rounding down go back to the segments that lost the most.
 *
 *  An all-zero input returns all zeros rather than dividing by it. */
export function shares(counts: readonly number[]): number[] {
  const total = counts.reduce((a, b) => a + b, 0);
  if (total <= 0) return counts.map(() => 0);

  const exact = counts.map((c) => (c / total) * 100);
  const floors = exact.map(Math.floor);
  let remaining = 100 - floors.reduce((a, b) => a + b, 0);

  /* Hand the leftover units out in order of what each segment lost, biggest
   * loser first. Ties go to the earlier index, which keeps the result stable
   * for identical inputs instead of depending on sort implementation. */
  const order = exact
    .map((value, i) => ({ i, lost: value - floors[i]! }))
    .sort((a, b) => b.lost - a.lost || a.i - b.i);

  const out = [...floors];
  for (const { i } of order) {
    if (remaining <= 0) break;
    out[i]!+= 1;
    remaining -= 1;
  }
  return out;
}

/** Bar widths as percentages of the largest value, not of the total — a demand
 *  chart answers "how does this compare to the top one", and scaling to the sum
 *  makes every bar short as soon as there are more than a handful.
 *
 *  `floor` keeps a real but tiny value visible: one mention beside a skill with
 *  forty would otherwise round to a bar too thin to see, which reads as zero.
 *  A genuine zero still gets zero — the floor applies to values above it. */
export function barScale(counts: readonly number[], floor = 4): number[] {
  const max = Math.max(0, ...counts);
  if (max <= 0) return counts.map(() => 0);
  return counts.map((c) => (c <= 0 ? 0 : Math.max(floor, Math.round((c / max) * 100))));
}
