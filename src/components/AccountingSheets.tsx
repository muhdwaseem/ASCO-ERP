// Accounting tool sheets for live companies — a header strip with parameters/actions over a
// read-only grid fed by the ASCO API:
//    cc-pnl           Cost Centre / Project P&L  GET  /reports/cost-centre-pnl?from&to
//    gratuity         Gratuity (UAE EOSB)        GET  /gratuity?asOf   · POST /gratuity/provision, /gratuity/settle
//    dep-schedule     Depreciation schedule      GET  /fixed-assets/{id}/schedule · POST /fixed-assets/{id}/dispose
//    prepay-schedule  Prepayment schedule        GET  /prepayments     · POST /prepayments/release
// New assets and prepayments are typed in the Fast Entry sheets (EntrySheets: asset-batch, prepay-batch).
import { useState, useSyncExternalStore } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { Col, Row, RowMeta } from '../modules/registry';
import { api, type Lookups } from '../api/client';
import { Combo } from './EntrySheets';
import { d, m, meta, money, n, num, r2, resolve, t } from '../modules/sheetKit';

type J = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
type Acct = { id: number; code: string; name: string };

// ---------------------------------------------------------------- parameter store
interface Params {
  from: string; to: string;
  asOf: string; gExpense: string; leaver: string; leaveDate: string; leaveBank: string;
  asset: string; dispDate: string; dispProceeds: string; dispBank: string; dispGl: string;
  upTo: string;
}
let params: Params = { from: '', to: '', asOf: '', gExpense: '', leaver: '', leaveDate: '', leaveBank: '', asset: '', dispDate: '', dispProceeds: '', dispBank: '', dispGl: '', upTo: '' };
let version = 0;
const subs = new Set<() => void>();
const setParams = (patch: Partial<Params>) => { params = { ...params, ...patch }; version++; subs.forEach((f) => f()); };
/** Re-render the caller whenever a tool sheet's parameters change. */
export function useToolStores() {
  return useSyncExternalStore((cb) => { subs.add(cb); return () => subs.delete(cb); }, () => version);
}

// ---------------------------------------------------------------- helpers
const label = (a: Acct) => `${a.code} · ${a.name}`;
const find = <T extends { code: string }>(list: T[], lbl: (x: T) => string, text: string) => resolve(list, lbl, (x) => x.code, text);
const expenseAccts = (L?: Lookups) => (L?.accounts ?? []).filter((a) => a.type === 'Expense');
const plAccts = (L?: Lookups) => (L?.accounts ?? []).filter((a) => a.type === 'Expense' || a.type === 'Income');
const firstOfYear = (today: string) => `${today.slice(0, 4)}-01-01`;
const lastMonthEnd = (today: string) => { const x = new Date(`${today.slice(0, 7)}-01T00:00:00Z`); x.setUTCDate(0); return x.toISOString().slice(0, 10); };
const msgView = (title: string, msg: string, style: RowMeta['style'] = 'muted') => ({ columns: [t('msg', title, 640)], rows: [{ msg, ...meta(style) }] as Row[] });

// ================================================================ facade used by App
export const TOOL_SHEETS = ['cc-pnl', 'gratuity', 'dep-schedule', 'prepay-schedule'] as const;
export type ToolId = (typeof TOOL_SHEETS)[number];
export const isToolSheet = (id: string): id is ToolId => (TOOL_SHEETS as readonly string[]).includes(id);

/** The GET the grid needs (undefined until the header has enough to ask). */
export function toolPath(id: ToolId, today: string, assets?: J[]): string | undefined {
  if (id === 'cc-pnl') return `/reports/cost-centre-pnl?from=${params.from || firstOfYear(today)}&to=${params.to || today}`;
  if (id === 'gratuity') return `/gratuity?asOf=${params.asOf || today}`;
  if (id === 'prepay-schedule') return '/prepayments';
  const a = assets ? find(assets as { code: string }[], (x: J) => `${x.assetCode} · ${x.name}`, params.asset) as J | undefined : undefined;
  return a ? `/fixed-assets/${a.id}/schedule` : undefined;
}

/** Asset list for the depreciation-schedule picker (codes keyed as `code` for find()). */
export function useAssets(enabled: boolean, companyId: number | null | undefined) {
  const q = useQuery({ queryKey: ['sheet', companyId, '/fixed-assets'], queryFn: () => api.get<J[]>('/fixed-assets', companyId!), enabled: enabled && !!companyId });
  return q.data?.map((a) => ({ ...a, code: a.assetCode }));
}

export function toolView(id: ToolId, data: unknown, state: { pending: boolean; error?: string }) {
  const title = { 'cc-pnl': 'Cost Centre P&L', gratuity: 'Gratuity', 'dep-schedule': 'Depreciation Schedule', 'prepay-schedule': 'Prepayment Schedule' }[id];
  if (id === 'dep-schedule' && !params.asset) return msgView(title, 'Pick an asset in the strip above to see its month-by-month depreciation.');
  if (state.error) return msgView(title, state.error, 'warn');
  if (state.pending) return msgView(title, 'Loading…');
  if (data == null) return msgView(title, id === 'dep-schedule' ? 'Asset not found — pick one from the list.' : 'Loading…');
  const x = data as J;
  if (id === 'cc-pnl') return ccView(x);
  if (id === 'gratuity') return gratuityView(x);
  if (id === 'dep-schedule') return depView(x);
  return prepayView(x as J[]);
}

// ---------------------------------------------------------------- views
function ccView(x: J) {
  const centres = x.centres as { key: string; code: string; name: string }[];
  const columns: Col[] = [t('code', 'Account', 80), t('name', 'Particulars', 240), ...centres.map((c) => m(`cc_${c.key}`, c.key === '_none' ? 'Unassigned' : `${c.code} ${c.name}`, 130)), m('total', 'Total', 130)];
  const line = (r: J): Row => {
    const out: Row = { code: r.code, name: r.name, ...meta(undefined, 1) };
    let tot = 0;
    for (const c of centres) { const v = r.amounts[c.key]; if (v) { out[`cc_${c.key}`] = r2(v); tot += v; } }
    out.total = r2(tot);
    return out;
  };
  const sum = (rows: J[], key: string) => r2(rows.reduce((a, r) => a + (r.amounts[key] ?? 0), 0));
  const totals = (rows: J[], name: string, style: RowMeta['style']): Row => {
    const out: Row = { name, ...meta(style, undefined, true) };
    let tot = 0;
    for (const c of centres) { const v = sum(rows, c.key); out[`cc_${c.key}`] = v; tot += v; }
    out.total = r2(tot);
    return out;
  };
  const inc = x.income as J[], exp = x.expense as J[];
  const net: Row = { name: `Net profit / (loss) — ${x.from} to ${x.to}`, ...meta('grand') };
  let ntot = 0;
  for (const c of centres) { const v = r2(sum(inc, c.key) - sum(exp, c.key)); net[`cc_${c.key}`] = v; ntot += v; }
  net.total = r2(ntot);
  if (!centres.length) return msgView('Cost Centre P&L', `No income or expense posted between ${x.from} and ${x.to}.`);
  return {
    columns,
    rows: [
      { name: 'Income', ...meta('group') }, ...inc.map(line), totals(inc, 'Total income', 'total'),
      { name: 'Expenses', ...meta('group') }, ...exp.map(line), totals(exp, 'Total expenses', 'total'),
      net,
    ] as Row[],
  };
}

function gratuityView(x: J) {
  const columns: Col[] = [t('employeeCode', 'Code', 70), t('fullName', 'Employee', 190), t('costCenter', 'Cost Ctr', 80), d('joiningDate', 'Joined'), m('basicSalary', 'Basic', 100),
    n('years', 'Years', 64), n('days', 'Days Earned', 90), m('accrued', 'Provision Required', 140), m('payable', 'Payable if Leaving', 140), t('note', 'Note', 220)];
  const rows: Row[] = (x.rows as J[]).map((r) => ({
    ...r, note: !r.eligible ? 'Not gratuity-eligible' : r.years < 1 ? 'Under 1 year — accrues, nothing payable yet' : r.years > 5 ? '30 days/year after year 5' : '',
    ...(r.eligible ? {} : meta('muted')),
  }));
  const short = r2(x.shortfall);
  return {
    columns,
    rows: [
      ...rows,
      { fullName: `Total — ${rows.length} active employees as of ${x.asOf}`, basicSalary: r2(rows.reduce((a, r) => a + (r.basicSalary as number), 0)), accrued: r2((x.rows as J[]).reduce((a, r) => a + r.accrued, 0)), payable: r2((x.rows as J[]).reduce((a, r) => a + r.payable, 0)), ...meta('total', undefined, true) },
      { fullName: 'Settled leavers not yet paid', accrued: r2(x.unpaidLeavers), ...meta(undefined, 1) },
      { fullName: 'Provision required', accrued: r2(x.required), ...meta('total') },
      { fullName: `Held in the books (${x.provisionAccount ?? '21040 — created on first provision'})`, accrued: r2(x.held), ...meta(undefined, 1) },
      { fullName: short > 0 ? 'Shortfall — post the month-end provision' : short < 0 ? 'Excess — the next provision run releases it' : 'Provision is up to date ✓', accrued: short, ...meta(Math.abs(short) >= 0.01 ? 'warn' : 'grand') },
    ] as Row[],
  };
}

function depView(x: J) {
  const columns: Col[] = [n('no', 'No', 50), t('month', 'Month', 90), m('opening', 'Opening NBV', 130), m('depreciation', 'Depreciation', 120), m('accumulated', 'Accumulated', 130), m('closing', 'Closing NBV', 130), t('status', 'Status', 180)];
  const rows = (x.rows as J[]).map((r) => ({ ...r, ...(r.status === 'Planned' ? meta('muted') : {}) }));
  const a = x.asset;
  const posted = (x.rows as J[]).filter((r) => r.status !== 'Planned');
  return {
    columns,
    rows: [
      ...rows,
      { month: 'Total', depreciation: r2((x.rows as J[]).reduce((s, r) => s + r.depreciation, 0)), ...meta('total', undefined, true) },
      { month: `${a.assetCode} ${a.name} — ${posted.length} of ${x.rows.length} months posted · NBV now ${money(a.netBookValue)}${a.status === 'Disposed' ? ` · disposed ${a.disposalDate}` : ''}`, ...meta(a.status === 'Disposed' ? 'warn' : 'muted') },
    ] as Row[],
  };
}

function prepayView(list: J[]) {
  if (!list.length) return msgView('Prepayment Schedule', 'No prepayments yet — add them in Finance → New Prepayments (one per row).');
  const thisMonth = new Date().toISOString().slice(0, 7);
  const columns: Col[] = [t('no', 'Prepayment / Month', 150), t('description', 'Description', 230), t('vendor', 'Vendor', 160), m('amount', 'Amount', 110), m('released', 'Released', 110), m('remaining', 'Remaining', 110), t('accounts', 'Expense → from Prepaid', 300), t('status', 'Status', 170)];
  const rows: Row[] = [];
  for (const p of list) {
    rows.push({ no: p.prepaymentNo, description: p.description, vendor: p.vendor ?? '', amount: p.amount, released: p.released, remaining: p.remaining,
      accounts: `${p.expenseAccount} ← ${p.prepaidAccount}${p.costCenter ? ` · ${p.costCenter}` : ''}`, status: `${p.status} · ${p.monthsReleased}/${p.months}`, ...meta('group') });
    for (const s of p.schedule as J[]) {
      const due = !s.voucherNo && s.month <= thisMonth && p.status === 'Active';
      rows.push({ no: s.month, description: `Month ${s.no} of ${p.months}`, amount: s.amount, status: s.voucherNo ? `✓ Posted ${s.voucherNo}` : due ? 'Due — run the release' : 'Planned',
        ...(s.voucherNo ? {} : due ? meta('warn', 1) : meta('muted', 1)), ...(s.voucherNo ? meta(undefined, 1) : {}) });
    }
  }
  const tot = (k: string) => r2(list.reduce((a, p) => a + p[k], 0));
  rows.push({ no: 'Total', amount: tot('amount'), released: tot('released'), remaining: tot('remaining'), ...meta('grand') });
  return { columns, rows };
}

// ---------------------------------------------------------------- header strips
export function ToolHeader({ id, L, data, assets, companyId, today, canPost, notify }: {
  id: ToolId; L: Lookups | undefined; data: unknown; assets?: J[]; companyId: number; today: string; canPost: boolean;
  notify: (msg: string, kind?: 'ok' | 'err') => void;
}) {
  useToolStores();
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const p = params;
  const act = async (fn: () => Promise<string>) => {
    if (!canPost) { notify('Your role in this company is read-only', 'err'); return; }
    setBusy(true);
    notify('Posting…');
    try { notify(await fn()); await qc.invalidateQueries(); } catch (e) { notify(e instanceof Error ? e.message : String(e), 'err'); } finally { setBusy(false); }
  };
  const banks = L?.bankAccounts ?? [];

  if (id === 'cc-pnl') return (
    <div className="entry-header">
      <label>From <input type="date" value={p.from || firstOfYear(today)} onChange={(e) => setParams({ from: e.target.value })} /></label>
      <label>To <input type="date" value={p.to || today} onChange={(e) => setParams({ to: e.target.value })} /></label>
      <span className="hint">Income and expenses from posted vouchers, one column per cost centre / project. Tag lines with a cost centre in any Fast Entry sheet.</span>
    </div>
  );

  if (id === 'gratuity') {
    const asOf = p.asOf || today;
    const emps = ((data as J | undefined)?.rows ?? []) as J[];
    const empLabel = (e: J) => `${e.employeeCode} · ${e.fullName}`;
    const exp = find(expenseAccts(L), label, p.gExpense) ?? (!p.gExpense ? expenseAccts(L).find((a) => /gratuity|end of service|eosb/i.test(a.name)) ?? expenseAccts(L).find((a) => /salar/i.test(a.name)) : undefined);
    const expText = p.gExpense || (exp ? label(exp) : '');
    return (
      <div className="entry-header">
        <label>As of <input type="date" value={asOf} onChange={(e) => setParams({ asOf: e.target.value })} /></label>
        <label style={{ minWidth: 230 }}>Gratuity expense account
          <Combo value={expText} options={expenseAccts(L).map(label)} placeholder="Expense account" onChange={(v) => setParams({ gExpense: v })} />
        </label>
        <button className="btn primary" disabled={busy} onClick={() => act(async () => {
          if (!exp) throw new Error('Choose the gratuity expense account.');
          const r = await api.post<J>('/gratuity/provision', { asOf, expenseAccountId: exp.id }, companyId);
          return r.voucher ? `Posted ${r.voucher}: provision ${r.amount > 0 ? 'topped up' : 'released'} by ${money(Math.abs(r.amount))} (required ${money(r.required)})` : r.message;
        })}>Post provision</button>
        <span className="hint" style={{ borderLeft: '1px solid #cfe3d6', paddingLeft: 12 }}>Leaver:</span>
        <label style={{ minWidth: 220 }}>Employee leaving
          <Combo value={p.leaver} options={emps.map(empLabel)} placeholder="Type name or code…" onChange={(v) => setParams({ leaver: v })} />
        </label>
        <label>Last day <input type="date" value={p.leaveDate || today} onChange={(e) => setParams({ leaveDate: e.target.value })} /></label>
        <label style={{ minWidth: 200 }}>Pay from (optional)
          <Combo value={p.leaveBank} options={banks.map(label)} placeholder="Blank = pay later" onChange={(v) => setParams({ leaveBank: v })} />
        </label>
        <button className="btn ghost" disabled={busy} onClick={() => act(async () => {
          const e = find(emps.map((x) => ({ ...x, code: x.employeeCode as string })), empLabel, p.leaver) as J | undefined;
          if (!e) throw new Error('Choose the employee who is leaving.');
          if (!exp) throw new Error('Choose the gratuity expense account.');
          const bank = p.leaveBank ? find(banks, label, p.leaveBank) : undefined;
          if (p.leaveBank && !bank) throw new Error('Choose the bank/cash account to pay from.');
          const r = await api.post<J>('/gratuity/settle', { employeeId: e.id, leavingDate: p.leaveDate || today, expenseAccountId: exp.id, bankAccountId: bank?.id ?? null }, companyId);
          setParams({ leaver: '' });
          return `${r.employee}: gratuity ${money(r.amount)} for ${r.years} years${r.utilised ? ` · met from provision ${r.utilised}` : ''}${r.paid ? ` · paid ${r.paid}` : ' · payable (not yet paid)'}`;
        })}>Settle leaver</button>
      </div>
    );
  }

  if (id === 'dep-schedule') {
    const list = assets ?? [];
    const aLabel = (a: J) => `${a.assetCode} · ${a.name}`;
    const a = find(list as { code: string }[], aLabel, p.asset) as J | undefined;
    const info = (data as J | undefined)?.asset;
    return (
      <div className="entry-header">
        <label style={{ minWidth: 260 }}>Asset
          <Combo value={p.asset} options={list.map(aLabel)} placeholder="Type asset name or code…" onChange={(v) => setParams({ asset: v })} />
          {info && <span className="picked">{money(info.purchaseCost)} over {info.usefulLifeMonths} months · {money(info.monthly)}/month · {info.costCenter ?? 'no cost centre'} · {info.status}</span>}
        </label>
        <button className="btn ghost" disabled={busy} onClick={() => act(async () => {
          const r = await api.post<J>('/fixed-assets/depreciation', {}, companyId);
          return r.voucher ? `Posted depreciation ${r.voucher} for this month (all assets)` : r.message;
        })}>Run this month's depreciation</button>
        <span className="hint" style={{ borderLeft: '1px solid #cfe3d6', paddingLeft: 12 }}>Sell / scrap:</span>
        <label>Date <input type="date" value={p.dispDate || today} onChange={(e) => setParams({ dispDate: e.target.value })} /></label>
        <label>Proceeds <input style={{ width: 100 }} value={p.dispProceeds} placeholder="0" onChange={(e) => setParams({ dispProceeds: e.target.value })} /></label>
        <label style={{ minWidth: 190 }}>Received into
          <Combo value={p.dispBank} options={banks.map(label)} placeholder="Bank (if proceeds)" onChange={(v) => setParams({ dispBank: v })} />
        </label>
        <label style={{ minWidth: 200 }}>Gain / loss account
          <Combo value={p.dispGl} options={plAccts(L).map(label)} placeholder="Income or expense a/c" onChange={(v) => setParams({ dispGl: v })} />
        </label>
        <button className="btn ghost" disabled={busy || !a} onClick={() => act(async () => {
          const gl = find(plAccts(L), label, p.dispGl);
          if (!gl) throw new Error('Choose the gain/loss on disposal account.');
          const proceeds = num(p.dispProceeds);
          const bank = p.dispBank ? find(banks, label, p.dispBank) : undefined;
          if (proceeds > 0 && !bank) throw new Error('Choose the bank the sale proceeds went into.');
          const r = await api.post<J>(`/fixed-assets/${a!.id}/dispose`, { date: p.dispDate || today, proceeds, bankAccountId: bank?.id ?? null, gainLossAccountId: gl.id }, companyId);
          return `Disposed ${a!.assetCode} — posted ${r.voucher}`;
        })}>Dispose asset</button>
      </div>
    );
  }

  // prepay-schedule
  const upTo = p.upTo || today.slice(0, 7);
  return (
    <div className="entry-header">
      <label>Release up to <input type="month" value={upTo} onChange={(e) => setParams({ upTo: e.target.value })} /></label>
      <button className="btn primary" disabled={busy} onClick={() => act(async () => {
        const r = await api.post<J>('/prepayments/release', { upTo: `${upTo}-01` }, companyId);
        return r.released ? `Released ${r.released} month(s) — posted ${r.vouchers.join(', ')}` : r.message;
      })}>Run prepayment release</button>
      <span className="hint">Posts Dr expense / Cr prepaid for every month due, dated at each month-end. Add new prepayments in Finance → New Prepayments. Last month-end: {lastMonthEnd(today)}.</span>
    </div>
  );
}
