// Date filters for live reports. Each dated report declares how it takes dates:
//   range  — From/To          (server: ?from&to, or client-side on `field`)
//   asOf   — a single date    (server: ?asOf, or client-side on `field` ≤ date)
//   year   — calendar year    (server: ?year)
//   month  — one month        (server: ?date=YYYY-MM-01, e.g. the cash flow's fiscal period)
// `field` means the endpoint has no date parameter: rows are filtered in the browser.

export interface DateMode { mode: 'range' | 'asOf' | 'year' | 'month'; field?: string; /** asOf sent as from=to=date */ asRange?: boolean }
export interface DateState { from: string; to: string; asOf: string; year: string; month: string }

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const ymd = (y: number, m: number, d: number) => iso(new Date(y, m, d)); // m is 0-based; day 0 = last day of previous month

export function todayIso() { return iso(new Date()); }

export function defaultDates(today = todayIso()): DateState {
  return { from: `${today.slice(0, 4)}-01-01`, to: today, asOf: today, year: today.slice(0, 4), month: today.slice(0, 7) };
}

/** Quick picks, like Excel's date filters. */
export function rangePresets(today = todayIso()): { label: string; from: string; to: string }[] {
  const t = new Date(`${today}T00:00:00`);
  const y = t.getFullYear(), m = t.getMonth(), q = Math.floor(m / 3) * 3;
  return [
    { label: 'Today', from: today, to: today },
    { label: 'This month', from: ymd(y, m, 1), to: ymd(y, m + 1, 0) },
    { label: 'Last month', from: ymd(y, m - 1, 1), to: ymd(y, m, 0) },
    { label: 'This quarter', from: ymd(y, q, 1), to: ymd(y, q + 3, 0) },
    { label: 'Last quarter', from: ymd(y, q - 3, 1), to: ymd(y, q, 0) },
    { label: 'Year to date', from: ymd(y, 0, 1), to: today },
    { label: 'This year', from: ymd(y, 0, 1), to: ymd(y, 11, 31) },
    { label: 'Last year', from: ymd(y - 1, 0, 1), to: ymd(y - 1, 11, 31) },
    { label: 'Last 12 months', from: ymd(y, m - 11, 1), to: ymd(y, m + 1, 0) },
  ];
}
export function asOfPresets(today = todayIso()): { label: string; date: string }[] {
  const t = new Date(`${today}T00:00:00`);
  const y = t.getFullYear(), m = t.getMonth(), q = Math.floor(m / 3) * 3;
  return [
    { label: 'Today', date: today },
    { label: 'End of last month', date: ymd(y, m, 0) },
    { label: 'End of last quarter', date: ymd(y, q, 0) },
    { label: 'End of last year', date: ymd(y, 0, 0) },
  ];
}

/** The API path with the date parameters the endpoint takes. */
export function datedPath(path: string, dm: DateMode | undefined, s: DateState): string {
  if (!dm || dm.field) return path;
  const sep = path.includes('?') ? '&' : '?';
  if (dm.mode === 'range') return `${path}${sep}from=${s.from}&to=${s.to}`;
  if (dm.mode === 'asOf') return dm.asRange ? `${path}${sep}from=${s.asOf}&to=${s.asOf}` : `${path}${sep}asOf=${s.asOf}`;
  if (dm.mode === 'year') return `${path}${sep}year=${s.year}`;
  return `${path}${sep}date=${s.month}-01`;
}

/** Client-side filtering for list endpoints without date parameters. */
export function filterByDate(data: unknown, dm: DateMode | undefined, s: DateState): unknown {
  if (!dm?.field || !Array.isArray(data)) return data;
  const f = dm.field;
  return data.filter((x) => {
    const v = String((x as Record<string, unknown>)[f] ?? '').slice(0, 10);
    if (!v) return true;
    return dm.mode === 'asOf' ? v <= s.asOf : v >= s.from && v <= s.to;
  });
}

/** Human label for the active filter (status bar / CSV name). */
export function dateLabel(dm: DateMode | undefined, s: DateState): string {
  if (!dm) return '';
  if (dm.mode === 'range') return `${s.from} to ${s.to}`;
  if (dm.mode === 'asOf') return `as of ${s.asOf}`;
  if (dm.mode === 'year') return `year ${s.year}`;
  return `month ${s.month}`;
}

const R = (field?: string): DateMode => ({ mode: 'range', field });
const A = (field?: string): DateMode => ({ mode: 'asOf', field });

/** Which reports are dated, and how. Reports not listed show current figures. */
export const REPORT_DATES: Record<string, DateMode> = {
  // ledger & statements
  'general-ledger': R(), 'trial-balance': R(), pnl: R(), mis: R(), 'commission-report': R(),
  'balance-sheet': A(), 'chart-of-accounts': A(), 'cash-flow': { mode: 'month' },
  'rpt-monthly-pnl': { mode: 'year' }, 'rpt-ratios': R(), 'rpt-equity': R(), 'rpt-expenses-by-category': R(), 'rpt-account-type-summary': R(),
  // sales & purchases
  'rpt-sales-by-customer': R(), 'rpt-sales-by-item': R(), 'rpt-purchases-by-vendor': R(), 'rpt-purchases-by-item': R(),
  'rpt-sales-by-salesperson': R('date'), 'rpt-sales-summary': R('date'),
  'sales-invoices': R('date'), estimates: R('date'), 'delivery-notes': R('date'), receipts: R('date'), 'credit-notes': R('date'), transactions: R('invoiceDate'),
  'purchase-invoices': R('date'), payments: R('date'), 'debit-notes': R('date'), expenses: R('date'), 'expense-transactions': R('invoiceDate'),
  'voucher-register': R('date'),
  // receivables / payables ageing
  'ar-aging': A(), 'ap-aging': A(), outstanding: A(), 'rpt-ar-aging-details': A(), 'rpt-ap-aging-details': A('date'),
  // tax, banking, others
  'rpt-vat-return': R(), 'rpt-bank-book': R(), 'rpt-bank-balances': { mode: 'asOf', asRange: true },
  payroll: R('runDate'), 'audit-log': R('changedAtUtc'), 'ai-guardrails': R('createdAtUtc'), 'stock-moves': R('date'), trips: R('date'),
};
