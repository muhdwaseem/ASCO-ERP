// Tiny external store. Swapping to the real API later = replace mutate() with fetch + refetch.
import { useSyncExternalStore } from 'react';
import type { LedgerState } from './types';
import { seed } from './seed';

let state: LedgerState = seed();
let version = 0;
const listeners = new Set<() => void>();

export const store = {
  get: () => state,
  /** Runs fn against the live state; fn must validate before writing (posting functions do). */
  mutate<T>(fn: (s: LedgerState) => T): T {
    const result = fn(state);
    state = { ...state };
    version++;
    listeners.forEach((l) => l());
    return result;
  },
  reset() {
    state = seed();
    version++;
    listeners.forEach((l) => l());
  },
};

export function useLedger() {
  useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => version,
  );
  return state;
}
