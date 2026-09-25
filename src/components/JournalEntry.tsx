// Journal Voucher entry as an editable sheet — the "type into cells" feel of Excel,
// but posting still goes through the engine's balanced-voucher gate.
import { useSyncExternalStore } from 'react';
import type { Col, Row } from '../modules/registry';
import type { LedgerState } from '../engine/types';
import { postJournal, r2, PostingError } from '../engine/ledger';
import { store } from '../engine/store';
import { api } from '../api/client';

/** Resolves a typed account code to its name/postability — demo ledger or live chart of accounts. */
export type AccountResolver = (code: string) => { id?: number; name: string; isPostable: boolean } | undefined;
export const demoResolver = (s: LedgerState): AccountResolver => (code) => s.accounts.find((a) => a.code === code);

interface DraftLine { account: string; narration: string; party: string; debit: string; credit: string }
interface Draft { date: string; narration: string; lines: DraftLine[] }

const LINES = 10;
const blank = (): DraftLine => ({ account: '', narration: '', party: '', debit: '', credit: '' });
const fresh = (): Draft => ({ date: new Date().toISOString().slice(0, 10), narration: '', lines: Array.from({ length: LINES }, blank) });

let draft: Draft = fresh();
let ver = 0;
const subs = new Set<() => void>();
const set = (d: Draft) => { draft = d; ver++; subs.forEach((f) => f()); };

export function useJeDraft() {
  useSyncExternalStore((cb) => { subs.add(cb); return () => subs.delete(cb); }, () => ver);
  return draft;
}

export const JE_COLUMNS: Col[] = [
  { key: 'account', label: 'Account', width: 90 },
  { key: 'name', label: 'Account Name (auto)', width: 230 },
  { key: 'narration', label: 'Line Narration', width: 260 },
  { key: 'party', label: 'Party (optional)', width: 120 },
  { key: 'debit', label: 'Debit', width: 120, type: 'money' },
  { key: 'credit', label: 'Credit', width: 120, type: 'money' },
];
const KEYS: (keyof DraftLine | null)[] = ['account', null, 'narration', 'party', 'debit', 'credit'];

const num = (v: string) => (v.trim() === '' ? 0 : Number(v.replace(/,/g, '')) || 0);

export function jeRows(resolve: AccountResolver, d: Draft): Row[] {
  const lines: Row[] = d.lines.map((l) => {
    const known = resolve(l.account.trim());
    return {
      account: l.account, name: l.account ? (known ? (known.isPostable ? known.name : `⚠ ${known.name} is a header`) : '⚠ unknown account') : '',
      narration: l.narration, party: l.party,
      debit: l.debit === '' ? undefined : num(l.debit), credit: l.credit === '' ? undefined : num(l.credit),
    };
  });
  const dr = r2(d.lines.reduce((a, l) => a + num(l.debit), 0));
  const cr = r2(d.lines.reduce((a, l) => a + num(l.credit), 0));
  const diff = r2(dr - cr);
  return [
    ...lines,
    { name: 'Total', debit: dr, credit: cr, _meta: { style: 'grand', formula: {} } },
    { name: diff === 0 ? (dr ? 'Balanced ✓ — ready to post' : 'Enter lines') : 'Out of balance', debit: diff > 0 ? undefined : -diff || undefined, credit: diff > 0 ? diff : undefined, _meta: { style: diff === 0 ? 'muted' : 'warn' } },
  ];
}

export const jeEditable = (r: number, c: number) => r < LINES && KEYS[c] !== null && c < KEYS.length;

export function jeEdit(r: number, c: number, value: string) {
  const key = KEYS[c];
  if (!key || r >= LINES) return;
  const lines = draft.lines.map((l, i) => (i === r ? { ...l, [key]: value } : l));
  set({ ...draft, lines });
}

export function postDraft(user: string): { ok: boolean; msg: string } {
  const d = draft;
  const lines = d.lines
    .filter((l) => l.account.trim())
    .map((l) => ({ account: l.account.trim(), narration: l.narration || undefined, party: l.party || undefined, debit: num(l.debit), credit: num(l.credit) }));
  try {
    const v = store.mutate((s) => postJournal(s, d.date, d.narration || 'Manual journal', lines, user));
    set(fresh());
    return { ok: true, msg: `Posted ${v.voucherNo} — ${lines.length} lines, ${r2(lines.reduce((a, l) => a + l.debit, 0)).toFixed(2)} AED` };
  } catch (e) {
    return { ok: false, msg: e instanceof PostingError ? e.message : String(e) };
  }
}

/** Live mode: post (or save as draft) through the ASCO API → C-ERP JournalService. */
export async function postDraftLive(resolve: AccountResolver, companyId: number, asDraft: boolean): Promise<{ ok: boolean; msg: string }> {
  const d = draft;
  const used = d.lines.filter((l) => l.account.trim());
  const unknown = used.find((l) => !resolve(l.account.trim())?.id);
  if (!used.length) return { ok: false, msg: 'Enter at least two lines.' };
  if (unknown) return { ok: false, msg: `Unknown or header account "${unknown.account}".` };
  try {
    const res = await api.post<{ number: string; status: string }>('/vouchers', {
      date: d.date, narration: d.narration || 'Manual journal', reference: null, draft: asDraft,
      lines: used.map((l) => ({ accountId: resolve(l.account.trim())!.id, costCenterId: null, description: l.narration || null, debit: num(l.debit), credit: num(l.credit) })),
    }, companyId);
    set(fresh());
    return { ok: true, msg: `${asDraft ? 'Saved draft' : 'Posted'} ${res.number}` };
  } catch (e) {
    return { ok: false, msg: e instanceof Error ? e.message : String(e) };
  }
}

export function JeHeader({ onPost, onDraft, hint }: { onPost: () => void; onDraft?: () => void; hint?: string }) {
  const d = useJeDraft();
  const s = store.get();
  return (
    <div className="je-header">
      <label>Voucher date <input type="date" value={d.date} onChange={(e) => set({ ...d, date: e.target.value })} /></label>
      <label className="grow">Narration <input value={d.narration} placeholder="e.g. Accrue September audit fees" onChange={(e) => set({ ...d, narration: e.target.value })} /></label>
      <span className="je-hint">{hint ?? `Postable accounts: ${s.accounts.filter((a) => a.isPostable).slice(0, 4).map((a) => a.code).join(', ')}… · see Chart of Accounts`}</span>
      <button className="btn ghost" onClick={() => set(fresh())}>Clear</button>
      {onDraft && <button className="btn ghost" onClick={onDraft}>Save draft</button>}
      <button className="btn primary" onClick={onPost}>Post voucher</button>
    </div>
  );
}

