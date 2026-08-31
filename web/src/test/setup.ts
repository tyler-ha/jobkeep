import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

/* Testing Library unmounts between tests automatically, but only when Vitest's
 * globals are enabled — its auto-cleanup hooks into a global afterEach that
 * does not exist here. Globals are deliberately off (see vite.config.ts), so
 * the hook is registered explicitly instead. Without it the second test in a
 * file queries a document still holding the first test's DOM, and the failure
 * looks like a duplicate-element bug in the component. */
afterEach(cleanup);
