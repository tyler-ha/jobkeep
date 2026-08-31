import type { ApplicationStatus } from '../lib/api';

/* The status chip. In components/ because a second screen needs it — Job post
 * as well as Applications — which is the bar the front-end structure rule sets.
 *
 * The colour lives entirely in shell.css keyed off data-status. Closed statuses
 * get the neutral rather than the alert: Rejected is a state the user can move
 * back out of, and colouring it like an error would say otherwise. */
export function StatusChip({ status }: { status: ApplicationStatus }) {
  return (
    <span className="chip" data-status={status}>
      {status}
    </span>
  );
}
