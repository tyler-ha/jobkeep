/* The curve behind the upload progress bar.
 *
 * It lives here rather than inside the screen for the same reason `chart.ts`
 * does: this is arithmetic a UI depends on that can be wrong without looking
 * wrong. A bar that fills is still a bar that fills, even when it reaches 100%
 * and then sits there for another minute.
 *
 * THE TRADEOFF, because it is the honest part and the interview material.
 *
 * This bar models the WAIT, not the transfer. The wait is the model: llama3.2:3b
 * on CPU, up to `ModelOptions.TimeoutSeconds` (180), plus the cost of loading
 * the weights on the first call after boot. A byte-transfer bar would show 100%
 * within a frame and then lie for three minutes, and `lib/api.ts` uses plain
 * `fetch`, which cannot report request-body progress anyway.
 *
 * The client genuinely cannot know when the model will answer. Given that, the
 * two honest options were a spinner or a decelerating estimate, and the user
 * chose the estimate after being told it cannot know. So it is built to be as
 * defensible as possible: it decelerates rather than claiming to be nearly
 * done, it never reaches 100% until the response actually lands, and it is
 * labelled to the screen reader as an estimate.
 *
 * WHAT PHASE 6.5 GROUP 6 CHANGED, and what it did not.
 *
 * This comment used to end by saying that becoming truthful needed a 202 + poll,
 * a new `ImportStatus` value AND A MIGRATION. The last of those was wrong, and
 * checking it is what made the change affordable: `document_imports.Status` is
 * `HasConversion<string>().HasMaxLength(20)` with no CHECK constraint, so a new
 * enum value is a code change and nothing else. That was already known at 13.2c,
 * when `CommitFailed` was added for exactly this reason.
 *
 * So the redesign happened. `POST /imports` returns as soon as the text is
 * saved, the row sits in `Parsing`, and the review screen drives the model
 * through `POST /imports/{id}/reparse` — the endpoint this comment already
 * pointed at as the place it would start.
 *
 * The bar SURVIVES, because the wait did not disappear, it moved: it is now on
 * the review screen, beside the extracted text, rather than under the upload
 * button. What changed is that the client can finally observe the wait END —
 * the reparse response is the transition, rather than an inference — so the
 * estimate is bounded by a real event instead of running until something
 * happens. The curve below is unchanged and its two properties still hold.
 */

/** How long a parse usually takes. Measured during Phase 4: ~8.4s on a cold
 *  model, ~3.1s warm, and longer for a full job ad than for a CV. The median is
 *  set above the warm figure on purpose — an estimate that sprints and then
 *  stalls reads worse than one that is steady and slightly pessimistic. */
export const PARSE_MEDIAN_MS = 12_000;

/** Elapsed milliseconds → a fraction in [0, 1).
 *
 *  Exponential, so it is fast at the start where the user is watching and slow
 *  at the end where the wait actually lives. Two properties matter and both are
 *  pinned by the tests:
 *
 *  - It **never reaches 1.** Only the response sets 100%. A bar that arrives at
 *    the end and stops is a bar that has told the user the app is broken.
 *  - It **never stalls on a round number.** A bar that parks on 90% is the
 *    thing everybody has learned to distrust; this one keeps moving, just less.
 *
 *  At the median it reads ~75%, which is the claim being made: "usually done by
 *  about here", not "three quarters of the bytes have moved". */
export function estimateProgress(elapsedMs: number, medianMs: number = PARSE_MEDIAN_MS): number {
  if (!(elapsedMs > 0) || !(medianMs > 0)) return 0;
  return 1 - Math.exp((-1.4 * elapsedMs) / medianMs);
}
