// Shared helpers for building sheets: column builders, row styling, numbers and typed-text lookup.
// Every sheet module imports these instead of keeping its own copy.
import type { Col, RowMeta } from './registry';

export const t = (key: string, label: string, width = 120): Col => ({ key, label, width });
export const m = (key: string, label: string, width = 110): Col => ({ key, label, width, type: 'money' });
export const n = (key: string, label: string, width = 70): Col => ({ key, label, width, type: 'number' });
export const d = (key: string, label: string, width = 96): Col => ({ key, label, width, type: 'date' });

/** Row styling: `{ ...meta('total') }`, optional indent, `formula` marks a row the formula bar shows as =SUM(). */
export const meta = (style?: RowMeta['style'], indent?: number, formula?: boolean) => ({ _meta: { style, indent, formula: formula ? {} : undefined } as RowMeta });

export const r2 = (x: number) => Math.round(x * 100) / 100;
/** Typed cell text → number ('' → 0, commas allowed). */
export const num = (v = '') => (v.trim() === '' ? 0 : Number(v.replace(/,/g, '')) || 0);
export const money = (x: number) => x.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** Resolve typed text to a record: exact label, then code, then a unique "contains" match. */
export function resolve<T>(list: T[], label: (x: T) => string, code: (x: T) => string, text = ''): T | undefined {
  const q = text.trim().toLowerCase();
  if (!q) return undefined;
  return list.find((x) => label(x).toLowerCase() === q)
    ?? list.find((x) => code(x).toLowerCase() === q.split(' ')[0])
    ?? (() => { const hits = list.filter((x) => label(x).toLowerCase().includes(q)); return hits.length === 1 ? hits[0] : undefined; })();
}
