// Excel-style "fast entry" for live companies — type documents straight into the grid.
//
//  Document sheets (header strip + line cells, Ctrl+Enter posts):
//    invoice-entry  Sales invoice   → POST /sales-invoices   (or Save draft)
//    quote-entry    Quotation       → POST /estimates        (saved, not posted)
//    bill-entry     Purchase bill   → POST /purchase-invoices
//  Batch sheets (one document per row, "Post all" posts every ready row and reports per row):
//    receipt-batch  Customer receipts → POST /receipts
//    payment-batch  Vendor payments   → POST /vendor-payments
//    expense-batch  Direct expenses   → POST /expenses
//
// Cells that need a customer / vendor / item / account / invoice offer search-as-you-type
// (Grid optionsFor). Everything posts through the ASCO API → C-ERP's own services.
import { useState, useSyncExternalStore } from 'react';
import type { Col, Row, RowMeta } from '../modules/registry';
import { api, vatFraction, type Lookups } from '../api/client';

// ---------------------------------------------------------------- tiny store + helpers

function createStore<T>(init: () => T) {
  let value = init();
  const subs = new Set<() => void>();
  const set = (v: T) => { value = v; version++; subs.forEach((f) => f()); };
  return { get: () => value, set, reset: () => set(init()), subs };
}
let version = 0;
const allSubs = new Set<() => void>();
/** Re-render the caller whenever any entry sheet changes. */
export function useEntryStores() {
  return useSyncExternalStore((cb) => { allSubs.add(cb); return () => allSubs.delete(cb); }, () => version);
}
function store<T>(init: () => T) {
  const s = createStore(init);
  const set = (v: T) => { s.set(v); allSubs.forEach((f) => f()); };
  return { get: s.get, set, reset: () => set(init()) };
}

const num = (v = '') => (v.trim() === '' ? 0 : Number(v.replace(/,/g, '')) || 0);
const r2 = (n: number) => Math.round(n * 100) / 100;
const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const meta = (style?: RowMeta['style']) => ({ _meta: { style } as RowMeta });
const t = (key: string, label: string, width = 120): Col => ({ key, label, width });
const m = (key: string, label: string, width = 110): Col => ({ key, label, width, type: 'money' });
const n = (key: string, label: string, width = 70): Col => ({ key, label, width, type: 'number' });
const isDate = (s: string) => /^\d{4}-\d{2}-\d{2}$/.test(s);

type Party = { id: number; code: string; name: string; paymentTermsDays?: number };
type Item = Lookups['items'][number];
type Acct = { id: number; code: string; name: string };

const partyLabel = (p: Party) => `${p.code} · ${p.name}`;
const itemLabel = (i: Item) => `${i.code} · ${i.name}`;
const acctLabel = (a: Acct) => `${a.code} · ${a.name}`;

/** Resolve typed text to a record: exact label, then code, then a unique "contains" match. */
function resolve<T>(list: T[], label: (x: T) => string, code: (x: T) => string, text = ''): T | undefined {
  const q = text.trim().toLowerCase();
  if (!q) return undefined;
  return list.find((x) => label(x).toLowerCase() === q)
    ?? list.find((x) => code(x).toLowerCase() === q.split(' ')[0])
    ?? (() => { const hits = list.filter((x) => label(x).toLowerCase().includes(q)); return hits.length === 1 ? hits[0] : undefined; })();
}
const findParty = (list: Party[], text: string) => resolve(list, partyLabel, (p) => p.code, text);
const findAcct = (list: Acct[], text: string) => resolve(list, acctLabel, (a) => a.code, text);
const findItem = (L: Lookups, text: string) => resolve(L.items, itemLabel, (i) => i.code, text);
const findCc = (L: Lookups, text: string) => findAcct(L.costCenters, text);
const ccOptions = (L: Lookups) => L.costCenters.map(acctLabel);
const incomeAccounts = (L: Lookups) => L.accounts.filter((a) => a.type === 'Income');
const costAccounts = (L: Lookups) => L.accounts.filter((a) => a.type === 'Expense' || a.type === 'Asset');

/** Small search box with a suggestion list — used in the header strips. */
export function Combo({ value, options, placeholder, onChange }: { value: string; options: string[]; placeholder?: string; onChange: (v: string) => void }) {
  const [open, setOpen] = useState(false);
  const [hi, setHi] = useState(0);
  const matches = options.filter((o) => o.toLowerCase().includes(value.trim().toLowerCase())).slice(0, 8);
  const show = open && matches.length > 0 && !options.includes(value);
  return (
    <span className="combo" style={{ position: 'relative', display: 'block' }}>
      <input value={value} placeholder={placeholder} onFocus={() => setOpen(true)} onBlur={() => setTimeout(() => setOpen(false), 120)}
        onChange={(e) => { onChange(e.target.value); setOpen(true); setHi(0); }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') { e.preventDefault(); setHi((h) => Math.min(matches.length - 1, h + 1)); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setHi((h) => Math.max(0, h - 1)); }
          else if ((e.key === 'Enter' || e.key === 'Tab') && show) { onChange(matches[hi] ?? matches[0]); setOpen(false); }
        }} />
      {show && (
        <div className="cell-suggest" onMouseDown={(e) => e.preventDefault()}>
          {matches.map((o, i) => <div key={o} className={i === hi ? 'on' : ''} onMouseDown={() => { onChange(o); setOpen(false); }}>{o}</div>)}
        </div>
      )}
    </span>
  );
}

// ================================================================ DOCUMENT SHEETS (invoice / quote / bill)

type DocKind = 'invoice-entry' | 'quote-entry' | 'bill-entry';
interface Line { item: string; description: string; account: string; qty: string; rate: string; vat: string; cc: string }
interface DocDraft { party: string; date: string; validUntil: string; reference: string; narration: string; cc: string; lines: Line[] }
const DOC_LINES = 12;
const blankLine = (): Line => ({ item: '', description: '', account: '', qty: '', rate: '', vat: '', cc: '' });
const blankDoc = (): DocDraft => ({ party: '', date: '', validUntil: '', reference: '', narration: '', cc: '', lines: Array.from({ length: DOC_LINES }, blankLine) });
const docs: Record<DocKind, ReturnType<typeof store<DocDraft>>> = {
  'invoice-entry': store(blankDoc), 'quote-entry': store(blankDoc), 'bill-entry': store(blankDoc),
};

const DOC_CFG = {
  'invoice-entry': { party: 'Customer', sales: true, noun: 'invoice', postLabel: 'Post invoice', canDraft: true },
  'quote-entry': { party: 'Customer', sales: true, noun: 'quotation', postLabel: 'Save quotation', canDraft: false },
  'bill-entry': { party: 'Vendor', sales: false, noun: 'bill', postLabel: 'Post bill', canDraft: false },
} as const;

const docColumns = (k: DocKind): Col[] => [
  t('item', 'Item (type to search)', 210), t('description', 'Description', 240), t('account', DOC_CFG[k].sales ? 'Revenue Account' : 'Expense Account', 220),
  n('qty', 'Qty', 70), m('rate', 'Rate', 100), t('vat', 'VAT', 56), t('cc', 'Cost Centre / Project', 170), m('net', 'Net', 110), m('vatAmt', 'VAT Amt', 90), m('total', 'Total', 110),
];
const LINE_KEYS: (keyof Line)[] = ['item', 'description', 'account', 'qty', 'rate', 'vat', 'cc'];
const vatOf = (v: string) => (v.trim() === '' ? 0.05 : v.startsWith('0') ? 0 : 0.05);
const hasContent = (l: Line) => !!(l.item || l.description || l.account || l.qty || l.rate);
const docParties = (k: DocKind, L: Lookups): Party[] => (DOC_CFG[k].sales ? L.customers : L.vendors);
const docAccounts = (k: DocKind, L: Lookups): Acct[] => (DOC_CFG[k].sales ? incomeAccounts(L) : costAccounts(L));

function lineProblem(k: DocKind, L: Lookups, l: Line): string | null {
  if (l.item && !findItem(L, l.item)) return `item "${l.item}" not found`;
  if (!findAcct(docAccounts(k, L), l.account)) return `choose ${DOC_CFG[k].sales ? 'a revenue' : 'an expense'} account`;
  if (num(l.qty) <= 0) return 'enter a quantity';
  if (l.rate.trim() === '') return 'enter a rate';
  if (l.cc && !findCc(L, l.cc)) return `cost centre "${l.cc}" not found`;
  return null;
}

function docView(k: DocKind, L: Lookups) {
  const d = docs[k].get();
  let net = 0, vat = 0;
  const problems: string[] = [];
  const rows: Row[] = d.lines.map((l, i) => {
    if (!hasContent(l)) return { item: '', description: '', account: '', vat: '', cc: '' };
    const lnNet = r2(num(l.qty) * num(l.rate));
    const lnVat = r2(lnNet * vatOf(l.vat));
    net += lnNet; vat += lnVat;
    const p = lineProblem(k, L, l);
    if (p) problems.push(`Line ${i + 1}: ${p}`);
    return {
      item: l.item, description: l.description, account: l.account, qty: l.qty === '' ? undefined : num(l.qty), rate: l.rate === '' ? undefined : num(l.rate),
      vat: l.vat || '5%', cc: l.cc, net: lnNet, vatAmt: lnVat, total: r2(lnNet + lnVat), ...(p ? meta('warn') : {}),
    };
  });
  const cfg = DOC_CFG[k];
  const partyOk = !!findParty(docParties(k, L), d.party);
  const ccOk = !d.cc || !!findCc(L, d.cc);
  const status = !d.party ? `Choose the ${cfg.party.toLowerCase()} in the strip above`
    : !partyOk ? `${cfg.party} "${d.party}" not found`
    : !ccOk ? `Cost centre "${d.cc}" not found`
    : problems[0] ?? (net > 0 ? `Ready — press Ctrl+Enter (or ${cfg.postLabel})` : `Type the ${cfg.noun} lines`);
  rows.push({ description: 'Total', net: r2(net), vatAmt: r2(vat), total: r2(net + vat), _meta: { style: 'grand', formula: {} } });
  rows.push({ description: status, ...meta(problems.length || (d.party && !partyOk) || !ccOk ? 'warn' : 'muted') });
  return {
    columns: docColumns(k), rows,
    editable: (r: number, c: number) => r < DOC_LINES && c < LINE_KEYS.length,
    onEdit: (r: number, c: number, value: string) => docEdit(k, L, r, c, value),
    optionsFor: (r: number, c: number) => (r >= DOC_LINES ? undefined
      : c === 0 ? L.items.map(itemLabel) : c === 2 ? docAccounts(k, L).map(acctLabel) : c === 5 ? ['5%', '0%'] : c === 6 ? ccOptions(L) : undefined),
  };
}

function docEdit(k: DocKind, L: Lookups, r: number, c: number, value: string) {
  const d = docs[k].get();
  const key = LINE_KEYS[c];
  if (!key || r >= DOC_LINES) return;
  const line = { ...d.lines[r], [key]: key === 'qty' || key === 'rate' ? value.replace(/,/g, '') : value };
  if (key === 'vat') line.vat = value.trim() === '' ? '' : value.trim().startsWith('0') ? '0%' : '5%';
  if (key === 'item') {
    const it = findItem(L, value);
    if (it) {
      const sales = DOC_CFG[k].sales;
      line.item = itemLabel(it);
      line.description = line.description || it.name;
      const price = sales ? it.sellingPrice : it.costPrice;
      if (price) line.rate = String(price);
      const acct = L.accounts.find((a) => a.id === (sales ? it.salesAccountId : it.purchaseAccountId));
      if (acct && !line.account) line.account = acctLabel(acct);
      line.vat = vatFraction(it.vatRate) === 0 ? '0%' : '5%';
      if (!line.qty) line.qty = '1';
    }
  }
  if (key === 'account') { const a = findAcct(docAccounts(k, L), value); if (a) line.account = acctLabel(a); }
  if (key === 'cc') { const c = findCc(L, value); if (c) line.cc = acctLabel(c); }
  docs[k].set({ ...d, lines: d.lines.map((l, i) => (i === r ? line : l)) });
}

async function postDoc(k: DocKind, L: Lookups, companyId: number, defaultDate: string, asDraft: boolean) {
  const d = docs[k].get();
  const cfg = DOC_CFG[k];
  const party = findParty(docParties(k, L), d.party);
  if (!party) return { ok: false, msg: d.party ? `${cfg.party} "${d.party}" not found.` : `Choose the ${cfg.party.toLowerCase()} first.` };
  const used = d.lines.map((l, i) => ({ l, i })).filter(({ l }) => hasContent(l));
  if (!used.length) return { ok: false, msg: `Type at least one ${cfg.noun} line.` };
  if (d.cc && !findCc(L, d.cc)) return { ok: false, msg: `Cost centre "${d.cc}" not found.` };
  for (const { l, i } of used) { const p = lineProblem(k, L, l); if (p) return { ok: false, msg: `Line ${i + 1}: ${p}.` }; }
  const date = d.date || defaultDate;
  const lines = used.map(({ l }) => {
    const it = l.item ? findItem(L, l.item) : undefined;
    const acctId = findAcct(docAccounts(k, L), l.account)!.id;
    const base = { description: l.description || it?.name || 'Item', costCenterId: findCc(L, l.cc || d.cc)?.id ?? null, quantity: num(l.qty), unitPrice: num(l.rate), vatRate: vatOf(l.vat) };
    return cfg.sales ? { ...base, revenueAccountId: acctId, ...(k === 'invoice-entry' ? { itemId: it?.id ?? null } : {}) } : { ...base, expenseAccountId: acctId };
  });
  try {
    let res: { number: string };
    if (k === 'invoice-entry') {
      res = await api.post('/sales-invoices', { customerId: party.id, date, narration: d.narration || null, draft: asDraft, lines }, companyId);
    } else if (k === 'quote-entry') {
      const validUntil = d.validUntil || new Date(Date.parse(date) + 30 * 86_400_000).toISOString().slice(0, 10);
      if (validUntil < date) return { ok: false, msg: 'Valid-until can\'t be before the quotation date.' };
      res = await api.post('/estimates', { customerId: party.id, date, validUntil, narration: d.narration || null, lines }, companyId);
    } else {
      res = await api.post('/purchase-invoices', { vendorId: party.id, vendorRef: d.reference || null, date, narration: d.narration || null, lines }, companyId);
    }
    docs[k].reset();
    const what = k === 'quote-entry' ? `Saved quotation ${res.number} for ${party.name} — nothing posts until it's converted to an invoice`
      : asDraft ? `Saved draft ${res.number} for ${party.name} — submit/approve/post it from the Sales Invoice sheet`
      : `Posted ${res.number} for ${party.name} — GL voucher posted by C-ERP`;
    return { ok: true, msg: what };
  } catch (e) {
    return { ok: false, msg: e instanceof Error ? e.message : String(e) };
  }
}

function DocHeader({ k, L, defaultDate, onPost }: { k: DocKind; L: Lookups | undefined; defaultDate: string; onPost: (draft: boolean) => void }) {
  const d = docs[k].get();
  const cfg = DOC_CFG[k];
  const set = (patch: Partial<DocDraft>) => docs[k].set({ ...docs[k].get(), ...patch });
  const party = L ? findParty(docParties(k, L), d.party) : undefined;
  const date = d.date || defaultDate;
  const due = party?.paymentTermsDays != null ? new Date(Date.parse(date) + party.paymentTermsDays * 86_400_000).toISOString().slice(0, 10) : '';
  const validUntil = d.validUntil || (isDate(date) ? new Date(Date.parse(date) + 30 * 86_400_000).toISOString().slice(0, 10) : '');
  return (
    <div className="entry-header">
      <label style={{ minWidth: 280 }}>{cfg.party}
        <Combo value={d.party} placeholder="Type name or code…" options={L ? docParties(k, L).map(partyLabel) : []} onChange={(v) => set({ party: v })} />
        {party && k === 'invoice-entry' && <span className="picked">{party.paymentTermsDays}-day terms · due {due}</span>}
      </label>
      <label>Date <input type="date" value={date} onChange={(e) => set({ date: e.target.value })} /></label>
      {k === 'quote-entry' && <label>Valid until <input type="date" value={validUntil} onChange={(e) => set({ validUntil: e.target.value })} /></label>}
      {k === 'bill-entry' && <label>Vendor invoice no. <input value={d.reference} onChange={(e) => set({ reference: e.target.value })} /></label>}
      <label style={{ minWidth: 220 }}>Cost centre / Project
        <Combo value={d.cc} placeholder="Optional — for every line" options={L ? ccOptions(L) : []} onChange={(v) => set({ cc: v })} />
      </label>
      <label className="grow">Narration <input value={d.narration} placeholder="Optional" onChange={(e) => set({ narration: e.target.value })} /></label>
      <span className="hint">Type in the cells · Tab moves · Ctrl+Enter {k === 'quote-entry' ? 'saves' : 'posts'}</span>
      <button className="btn ghost" onClick={() => docs[k].reset()}>Clear</button>
      {cfg.canDraft && <button className="btn ghost" onClick={() => onPost(true)}>Save draft</button>}
      <button className="btn primary" onClick={() => onPost(false)}>{cfg.postLabel}</button>
    </div>
  );
}

// ================================================================ SETTLEMENT BATCHES (receipts / payments)

type SettleKind = 'receipt-batch' | 'payment-batch';
interface SettleRow { party: string; date: string; doc: string; amount: string; mode: string; reference: string; bank: string; result?: string; state?: 'posted' | 'error' }
const blankSettle = (): SettleRow => ({ party: '', date: '', doc: '', amount: '', mode: '', reference: '', bank: '' });
const settles: Record<SettleKind, ReturnType<typeof store<SettleRow[]>>> = {
  'receipt-batch': store(() => Array.from({ length: 15 }, blankSettle)),
  'payment-batch': store(() => Array.from({ length: 15 }, blankSettle)),
};
/** An unpaid invoice (receipts) or bill (payments), normalised from the API. */
export interface OpenDoc { id: number; partyCode: string; no: string; dueDate: string; due: number }
const SETTLE_CFG = {
  'receipt-batch': { party: 'Customer', doc: 'Against Invoice', bank: 'Deposit To', path: '/receipts', noun: 'receipt' },
  'payment-batch': { party: 'Vendor', doc: 'Against Bill', bank: 'Paid From', path: '/vendor-payments', noun: 'payment' },
} as const;
const MODES = ['BankTransfer', 'Cash', 'Cheque', 'Card', 'PostDatedCheque', 'Other'];
const modeLabel = (x: string) => x.replace(/([a-z])([A-Z])/g, '$1 $2');
const docLabel = (o: OpenDoc) => `${o.no} · due ${o.dueDate} · ${money(o.due)}`;
const settleColumns = (k: SettleKind): Col[] => [
  t('party', `${SETTLE_CFG[k].party} (type to search)`, 230), t('date', 'Date', 100), t('doc', SETTLE_CFG[k].doc, 260), m('outstanding', 'Outstanding', 110),
  m('amount', 'Amount', 110), t('mode', 'Mode', 120), t('reference', 'Reference / Cheque', 140), t('bank', SETTLE_CFG[k].bank, 210), t('result', 'Result', 280),
];
const S_KEYS: (keyof SettleRow | null)[] = ['party', 'date', 'doc', null, 'amount', 'mode', 'reference', 'bank', null];
const settleParties = (k: SettleKind, L: Lookups): Party[] => (k === 'receipt-batch' ? L.customers : L.vendors);
const findDoc = (open: OpenDoc[], text: string) => open.find((o) => docLabel(o) === text || o.no.toLowerCase() === text.trim().toLowerCase().split(' ')[0]);

function settleCheck(k: SettleKind, L: Lookups, open: OpenDoc[], x: SettleRow) {
  const party = findParty(settleParties(k, L), x.party);
  if (!party) return { error: x.party ? `${SETTLE_CFG[k].party.toLowerCase()} "${x.party}" not found` : `choose a ${SETTLE_CFG[k].party.toLowerCase()}` };
  const doc = x.doc ? findDoc(open.filter((o) => o.partyCode === party.code), x.doc) : undefined;
  if (x.doc && !doc) return { error: `"${x.doc}" isn't open for this ${SETTLE_CFG[k].party.toLowerCase()}` };
  if (!isDate(x.date)) return { error: 'date must be YYYY-MM-DD' };
  if (num(x.amount) <= 0) return { error: 'enter the amount' };
  const bank = findAcct(L.bankAccounts, x.bank);
  if (!bank) return { error: 'choose the bank/cash account' };
  if (!MODES.includes(x.mode)) return { error: 'choose a payment mode' };
  return { party, doc, bank };
}

// ================================================================ EXPENSE BATCH

interface ExpRow { date: string; vendor: string; description: string; account: string; amount: string; vat: string; bank: string; reference: string; cc: string; result?: string; state?: 'posted' | 'error' }
const blankExp = (): ExpRow => ({ date: '', vendor: '', description: '', account: '', amount: '', vat: '', bank: '', reference: '', cc: '' });
const expenses = store(() => Array.from({ length: 15 }, blankExp));
const EXPENSE_COLUMNS: Col[] = [
  t('date', 'Date', 100), t('vendor', 'Vendor (optional)', 200), t('description', 'Description', 230), t('account', 'Expense Account', 220),
  m('amount', 'Amount (net)', 110), t('vat', 'VAT', 56), m('gross', 'Total', 100), t('bank', 'Paid From', 200), t('reference', 'Reference', 120), t('cc', 'Cost Centre / Project', 160), t('result', 'Result', 260),
];
const E_KEYS: (keyof ExpRow | null)[] = ['date', 'vendor', 'description', 'account', 'amount', 'vat', null, 'bank', 'reference', 'cc', null];

function expCheck(L: Lookups, x: ExpRow) {
  if (!isDate(x.date)) return { error: 'date must be YYYY-MM-DD' };
  const vendor = x.vendor ? findParty(L.vendors, x.vendor) : undefined;
  if (x.vendor && !vendor) return { error: `vendor "${x.vendor}" not found` };
  const account = findAcct(costAccounts(L), x.account);
  if (!account) return { error: 'choose an expense account' };
  if (num(x.amount) <= 0) return { error: 'enter the amount' };
  const bank = findAcct(L.bankAccounts, x.bank);
  if (!bank) return { error: 'choose the bank/cash account' };
  const cc = x.cc ? findCc(L, x.cc) : undefined;
  if (x.cc && !cc) return { error: `cost centre "${x.cc}" not found` };
  return { vendor, account, bank, cc };
}

// ================================================================ shared batch rendering + posting

let defaultDate = '';
type BatchKind = SettleKind | 'expense-batch';

function batchStatus(state: string | undefined, result: string | undefined, err: string | undefined) {
  return state === 'posted' ? `✓ Posted ${result}` : state === 'error' ? `⚠ ${result}` : err ? `… ${err}` : 'Ready';
}

function settleView(k: SettleKind, L: Lookups, open: OpenDoc[]) {
  const rowsIn = settles[k].get();
  let total = 0, ready = 0;
  const rows: Row[] = rowsIn.map((x) => {
    if (!x.party && !x.amount && !x.doc) return { party: '', date: '', doc: '', mode: '', reference: '', bank: '', result: '' };
    const chk = settleCheck(k, L, open, x);
    const err = 'error' in chk ? chk.error : undefined;
    if (x.state === 'posted' || !err) total += num(x.amount);
    if (!x.state && !err) ready++;
    return {
      party: x.party, date: x.date, doc: x.doc, outstanding: 'doc' in chk ? chk.doc?.due : undefined, amount: x.amount === '' ? undefined : num(x.amount),
      mode: modeLabel(x.mode), reference: x.reference, bank: x.bank, result: batchStatus(x.state, x.result, err),
      ...(x.state === 'posted' ? meta('muted') : x.state === 'error' ? meta('warn') : {}),
    };
  });
  rows.push({ doc: `Total (${ready} ready to post)`, amount: r2(total), _meta: { style: 'grand', formula: {} } });
  return {
    columns: settleColumns(k), rows, ready,
    editable: (r: number, c: number) => r < rowsIn.length && S_KEYS[c] != null && rowsIn[r].state !== 'posted',
    onEdit: (r: number, c: number, value: string) => settleEdit(k, L, open, r, c, value),
    optionsFor: (r: number, c: number) => {
      if (r >= rowsIn.length) return undefined;
      if (c === 0) return settleParties(k, L).map(partyLabel);
      if (c === 2) { const p = findParty(settleParties(k, L), rowsIn[r].party); return open.filter((o) => !p || o.partyCode === p.code).map(docLabel); }
      if (c === 5) return MODES.map(modeLabel);
      if (c === 7) return L.bankAccounts.map(acctLabel);
      return undefined;
    },
  };
}

function settleEdit(k: SettleKind, L: Lookups, open: OpenDoc[], r: number, c: number, value: string) {
  const rows = settles[k].get();
  const key = S_KEYS[c];
  if (!key) return;
  const x: SettleRow = { ...rows[r], [key]: key === 'amount' ? value.replace(/,/g, '') : value, state: undefined, result: undefined };
  if (key === 'party') {
    const p = findParty(settleParties(k, L), value);
    if (p) x.party = partyLabel(p);
    if (!x.date) x.date = defaultDate;
    if (!x.mode) x.mode = 'BankTransfer';
    if (!x.bank && L.bankAccounts[0]) x.bank = acctLabel(L.bankAccounts[0]);
  }
  if (key === 'mode') x.mode = MODES.find((md) => md.toLowerCase() === value.replace(/\s/g, '').toLowerCase()) ?? value;
  if (key === 'bank') { const b = findAcct(L.bankAccounts, value); if (b) x.bank = acctLabel(b); }
  if (key === 'doc') { const o = findDoc(open, value); if (o) { x.doc = docLabel(o); if (!x.amount) x.amount = String(o.due); } }
  settles[k].set(rows.map((y, i) => (i === r ? x : y)));
}

function expView(L: Lookups) {
  const rowsIn = expenses.get();
  let total = 0, ready = 0;
  const rows: Row[] = rowsIn.map((x) => {
    if (!x.vendor && !x.description && !x.amount && !x.account) return { date: '', vendor: '', description: '', account: '', vat: '', bank: '', reference: '', cc: '', result: '' };
    const chk = expCheck(L, x);
    const err = 'error' in chk ? chk.error : undefined;
    const gross = r2(num(x.amount) * (1 + vatOf(x.vat)));
    if (x.state === 'posted' || !err) total += gross;
    if (!x.state && !err) ready++;
    return {
      date: x.date, vendor: x.vendor, description: x.description, account: x.account, amount: x.amount === '' ? undefined : num(x.amount),
      vat: x.vat || '5%', gross, bank: x.bank, reference: x.reference, cc: x.cc, result: batchStatus(x.state, x.result, err),
      ...(x.state === 'posted' ? meta('muted') : x.state === 'error' ? meta('warn') : {}),
    };
  });
  rows.push({ description: `Total (${ready} ready to post)`, gross: r2(total), _meta: { style: 'grand', formula: {} } });
  return {
    columns: EXPENSE_COLUMNS, rows, ready,
    editable: (r: number, c: number) => r < rowsIn.length && E_KEYS[c] != null && rowsIn[r].state !== 'posted',
    onEdit: (r: number, c: number, value: string) => expEdit(L, r, c, value),
    optionsFor: (r: number, c: number) => (r >= rowsIn.length ? undefined
      : c === 1 ? L.vendors.map(partyLabel) : c === 3 ? costAccounts(L).map(acctLabel) : c === 5 ? ['5%', '0%'] : c === 7 ? L.bankAccounts.map(acctLabel) : c === 9 ? ccOptions(L) : undefined),
  };
}

function expEdit(L: Lookups, r: number, c: number, value: string) {
  const rows = expenses.get();
  const key = E_KEYS[c];
  if (!key) return;
  const x: ExpRow = { ...rows[r], [key]: key === 'amount' ? value.replace(/,/g, '') : value, state: undefined, result: undefined };
  // first touch of a row fills the date and bank so a row is one or two cells of typing
  if (!x.date) x.date = defaultDate;
  if (!x.bank && L.bankAccounts[0]) x.bank = acctLabel(L.bankAccounts[0]);
  if (key === 'vendor') { const v = findParty(L.vendors, value); if (v) x.vendor = partyLabel(v); }
  if (key === 'account') { const a = findAcct(costAccounts(L), value); if (a) x.account = acctLabel(a); }
  if (key === 'bank') { const b = findAcct(L.bankAccounts, value); if (b) x.bank = acctLabel(b); }
  if (key === 'cc') { const c = findCc(L, value); if (c) x.cc = acctLabel(c); }
  if (key === 'vat') x.vat = value.trim() === '' ? '' : value.trim().startsWith('0') ? '0%' : '5%';
  expenses.set(rows.map((y, i) => (i === r ? x : y)));
}

async function postBatch(k: BatchKind, L: Lookups, open: OpenDoc[], companyId: number) {
  let posted = 0, failed = 0;
  const st = k === 'expense-batch' ? expenses : settles[k];
  const count = st.get().length;
  for (let i = 0; i < count; i++) {
    const x = st.get()[i] as SettleRow & ExpRow;
    if (x.state === 'posted') continue;
    let body: unknown, path: string;
    if (k === 'expense-batch') {
      if (!x.vendor && !x.description && !x.amount && !x.account) continue;
      const chk = expCheck(L, x);
      if ('error' in chk) continue;
      path = '/expenses';
      body = { vendorId: chk.vendor?.id ?? null, customerId: null, date: x.date, bankAccountId: chk.bank!.id, reference: x.reference || null,
        narration: x.description || 'Direct expense', payLater: false,
        lines: [{ expenseAccountId: chk.account!.id, costCenterId: chk.cc?.id ?? null, description: x.description || null, amount: num(x.amount), itemId: null, vatRate: vatOf(x.vat) }] };
    } else {
      if (!x.party && !x.amount && !x.doc) continue;
      const chk = settleCheck(k, L, open, x);
      if ('error' in chk) continue;
      path = SETTLE_CFG[k].path;
      body = { partyId: chk.party!.id, invoiceId: chk.doc?.id ?? null, date: x.date, bankAccountId: chk.bank!.id, amount: num(x.amount),
        narration: null, paymentMode: x.mode, referenceNo: x.reference || null };
    }
    try {
      const res = await api.post<{ number: string }>(path, body, companyId);
      st.set(st.get().map((y, j) => (j === i ? { ...y, state: 'posted', result: res.number } : y)) as never);
      posted++;
    } catch (e) {
      st.set(st.get().map((y, j) => (j === i ? { ...y, state: 'error', result: e instanceof Error ? e.message : String(e) } : y)) as never);
      failed++;
    }
  }
  const noun = k === 'expense-batch' ? 'expense' : SETTLE_CFG[k].noun;
  if (!posted && !failed) return { ok: false, msg: `No ready rows — the Result column says what each row still needs.` };
  return { ok: failed === 0, msg: `Posted ${posted} ${noun}(s)${failed ? `, ${failed} failed — see the Result column` : ''}` };
}

/** The row store behind a batch sheet, typed loosely so the header's row tools work for every kind. */
function batchRows(k: BatchKind) {
  const st = (k === 'expense-batch' ? expenses : settles[k]) as unknown as { get: () => { state?: string }[]; set: (v: object[]) => void; reset: () => void };
  const blank: () => object = k === 'expense-batch' ? blankExp : blankSettle;
  return { st, blank };
}

function BatchHeader({ k, ready, onPostAll }: { k: BatchKind; ready: number; onPostAll: () => void }) {
  const { st, blank } = batchRows(k);
  const hint = k === 'expense-batch'
    ? 'One expense per row · date & bank fill in · type the account (search) and amount · Ctrl+Enter posts all ready rows'
    : `One ${SETTLE_CFG[k].noun} per row · type a ${SETTLE_CFG[k].party.toLowerCase()}, pick the ${k === 'receipt-batch' ? 'invoice' : 'bill'} (amount fills in) · Ctrl+Enter posts all ready rows`;
  const noun = k === 'expense-batch' ? 'expense' : SETTLE_CFG[k].noun;
  return (
    <div className="entry-header">
      <span className="hint">{hint}</span>
      <span style={{ flex: 1 }} />
      <button className="btn ghost" onClick={() => st.set([...st.get(), ...Array.from({ length: 10 }, blank)])}>+ 10 rows</button>
      <button className="btn ghost" onClick={() => st.set([...st.get().filter((x) => x.state !== 'posted'), ...Array.from({ length: 3 }, blank)])}>Clear posted</button>
      <button className="btn ghost" onClick={() => st.reset()}>Clear all</button>
      <button className="btn primary" onClick={onPostAll}>Post {ready} ready {noun}{ready === 1 ? '' : 's'}</button>
    </div>
  );
}

// ================================================================ FORM BATCHES (one master record per row)
//    asset-batch    New fixed assets  → POST /fixed-assets   (register only — no GL posting)
//    prepay-batch   New prepayments   → POST /prepayments    (optionally posts the payment)

type FormKind = 'asset-batch' | 'prepay-batch';
type FormRow = Record<string, string> & { result?: string; state?: 'posted' | 'error' };
interface Field {
  key: string; label: string; width: number; type?: 'money' | 'number';
  options?: (L: Lookups) => string[];
  /** Typed text → canonical label (search-as-you-type columns). */
  pick?: (L: Lookups, text: string) => string | undefined;
  /** Filled in on the row's first edit. */
  init?: (L: Lookups) => string;
}
interface FormCfg { noun: string; hint: string; fields: Field[]; check: (L: Lookups, x: FormRow) => { error: string } | { path: string; body: object } }

const assetAccounts = (L: Lookups) => L.accounts.filter((a) => a.type === 'Asset');
const expenseOnly = (L: Lookups) => L.accounts.filter((a) => a.type === 'Expense');
const acctField = (key: string, label: string, list: (L: Lookups) => Acct[], init?: (L: Lookups) => Acct | undefined): Field => ({
  key, label, width: 220, options: (L) => list(L).map(acctLabel),
  pick: (L, v) => { const a = findAcct(list(L), v); return a && acctLabel(a); },
  init: init ? (L) => { const a = init(L); return a ? acctLabel(a) : ''; } : undefined,
});
const byName = (re: RegExp) => (list: Acct[]) => list.find((a) => re.test(a.name));
const ccField: Field = { key: 'cc', label: 'Cost Centre / Project', width: 160, options: ccOptions, pick: (L, v) => { const c = findCc(L, v); return c && acctLabel(c); } };
const need = (x: FormRow, key: string, msg: string) => (x[key]?.trim() ? null : msg);
const firstOfMonth = () => `${defaultDate.slice(0, 7)}-01`;

const FORM_CFG: Record<FormKind, FormCfg> = {
  'asset-batch': {
    noun: 'asset',
    hint: 'One asset per row · type name, cost and life in months · accounts fill in · Ctrl+Enter registers all ready rows (the purchase itself is the bill)',
    fields: [
      { key: 'name', label: 'Asset Name', width: 220 },
      { key: 'category', label: 'Category', width: 120 },
      { key: 'date', label: 'Purchase Date', width: 104, init: () => defaultDate },
      { key: 'cost', label: 'Cost', width: 110, type: 'money' },
      { key: 'salvage', label: 'Salvage', width: 90, type: 'money', init: () => '0' },
      { key: 'life', label: 'Life (months)', width: 90, type: 'number' },
      acctField('assetAcct', 'Asset Account', assetAccounts, (L) => byName(/equipment|fixed|furniture|vehicle|computer/i)(assetAccounts(L))),
      acctField('depAcct', 'Depreciation Expense A/c', expenseOnly, (L) => byName(/depreciat/i)(expenseOnly(L))),
      ccField,
    ],
    check: (L, x) => {
      const miss = need(x, 'name', 'type the asset name') ?? (isDate(x.date) ? null : 'date must be YYYY-MM-DD')
        ?? (num(x.cost) > 0 ? null : 'enter the cost') ?? (num(x.life) >= 1 ? null : 'enter the useful life in months');
      if (miss) return { error: miss };
      if (num(x.salvage) >= num(x.cost)) return { error: 'salvage must be less than cost' };
      const asset = findAcct(assetAccounts(L), x.assetAcct); if (!asset) return { error: 'choose the asset account' };
      const dep = findAcct(expenseOnly(L), x.depAcct); if (!dep) return { error: 'choose the depreciation expense account' };
      const cc = x.cc ? findCc(L, x.cc) : undefined; if (x.cc && !cc) return { error: `cost centre "${x.cc}" not found` };
      return { path: '/fixed-assets', body: { name: x.name.trim(), category: x.category || null, purchaseDate: x.date, purchaseCost: num(x.cost), salvageValue: num(x.salvage),
        usefulLifeMonths: Math.round(num(x.life)), assetAccountId: asset.id, depreciationExpenseAccountId: dep.id, costCenterId: cc?.id ?? null } };
    },
  },
  'prepay-batch': {
    noun: 'prepayment',
    hint: 'One prepayment per row · total, first month, number of months · leave "Paid From" blank if the bill is already posted to the prepaid account · Ctrl+Enter saves all ready rows',
    fields: [
      { key: 'description', label: 'Description', width: 220 },
      { key: 'vendor', label: 'Vendor (optional)', width: 190, options: (L) => L.vendors.map(partyLabel), pick: (L, v) => { const p = findParty(L.vendors, v); return p && partyLabel(p); } },
      { key: 'amount', label: 'Total Paid', width: 110, type: 'money' },
      { key: 'start', label: 'First Month', width: 104, init: firstOfMonth },
      { key: 'months', label: 'Months', width: 70, type: 'number', init: () => '12' },
      acctField('prepaid', 'Prepaid Account', assetAccounts, (L) => byName(/prepaid/i)(assetAccounts(L))),
      acctField('expense', 'Expense Account', expenseOnly),
      ccField,
      { key: 'paidFrom', label: 'Paid From (optional)', width: 200, options: (L) => L.bankAccounts.map(acctLabel), pick: (L, v) => { const b = findAcct(L.bankAccounts, v); return b && acctLabel(b); } },
    ],
    check: (L, x) => {
      const miss = need(x, 'description', 'describe it (e.g. Office rent 2027)') ?? (num(x.amount) > 0 ? null : 'enter the total paid')
        ?? (isDate(x.start) ? null : 'first month must be YYYY-MM-DD') ?? (num(x.months) >= 1 && num(x.months) <= 120 ? null : 'months must be 1–120');
      if (miss) return { error: miss };
      const vendor = x.vendor ? findParty(L.vendors, x.vendor) : undefined; if (x.vendor && !vendor) return { error: `vendor "${x.vendor}" not found` };
      const prepaid = findAcct(assetAccounts(L), x.prepaid); if (!prepaid) return { error: 'choose the prepaid (asset) account' };
      const expense = findAcct(expenseOnly(L), x.expense); if (!expense) return { error: 'choose the expense account' };
      const cc = x.cc ? findCc(L, x.cc) : undefined; if (x.cc && !cc) return { error: `cost centre "${x.cc}" not found` };
      const bank = x.paidFrom ? findAcct(L.bankAccounts, x.paidFrom) : undefined; if (x.paidFrom && !bank) return { error: 'choose the bank/cash account it was paid from' };
      return { path: '/prepayments', body: { description: x.description.trim(), vendorId: vendor?.id ?? null, startDate: x.start, months: Math.round(num(x.months)), amount: num(x.amount),
        prepaidAccountId: prepaid.id, expenseAccountId: expense.id, costCenterId: cc?.id ?? null, paidFromAccountId: bank?.id ?? null, paidDate: bank ? x.start : null } };
    },
  },
};
const blankForm = (): FormRow => ({});
const forms: Record<FormKind, ReturnType<typeof store<FormRow[]>>> = {
  'asset-batch': store(() => Array.from({ length: 12 }, blankForm)),
  'prepay-batch': store(() => Array.from({ length: 12 }, blankForm)),
};
const formUsed = (k: FormKind, x: FormRow) => FORM_CFG[k].fields.some((f) => x[f.key]?.trim() && !f.init);

function formView(k: FormKind, L: Lookups) {
  const cfg = FORM_CFG[k];
  const rowsIn = forms[k].get();
  let ready = 0;
  const columns: Col[] = [...cfg.fields.map((f) => ({ key: f.key, label: f.label, width: f.width, type: f.type })), t('result', 'Result', 280)];
  const rows: Row[] = rowsIn.map((x) => {
    if (!formUsed(k, x) && !x.state) return Object.fromEntries(cfg.fields.map((f) => [f.key, ''])) as Row;
    const chk = cfg.check(L, x);
    const err = 'error' in chk ? chk.error : undefined;
    if (!x.state && !err) ready++;
    const out: Row = {};
    for (const f of cfg.fields) out[f.key] = f.type && x[f.key] ? num(x[f.key]) : x[f.key] ?? '';
    return { ...out, result: x.state === 'posted' ? `✓ Saved ${x.result}` : batchStatus(x.state, x.result, err), ...(x.state === 'posted' ? meta('muted') : x.state === 'error' ? meta('warn') : {}) };
  });
  return {
    columns, rows, ready,
    editable: (r: number, c: number) => r < rowsIn.length && c < cfg.fields.length && rowsIn[r].state !== 'posted',
    onEdit: (r: number, c: number, value: string) => {
      const f = cfg.fields[c];
      const cur = forms[k].get();
      const { state: _s, result: _r, ...rest } = cur[r]; // eslint-disable-line @typescript-eslint/no-unused-vars
      const x: FormRow = { ...rest };
      if (!formUsed(k, cur[r])) for (const g of cfg.fields) if (g.init && !x[g.key]) x[g.key] = g.init(L);
      x[f.key] = f.type ? value.replace(/,/g, '') : value;
      if (f.pick) { const p = f.pick(L, value); if (p) x[f.key] = p; }
      forms[k].set(cur.map((y, i) => (i === r ? x : y)));
    },
    optionsFor: (r: number, c: number) => (r < rowsIn.length ? cfg.fields[c]?.options?.(L) : undefined),
  };
}

async function postForms(k: FormKind, L: Lookups, companyId: number) {
  const st = forms[k];
  let posted = 0, failed = 0;
  for (let i = 0; i < st.get().length; i++) {
    const x = st.get()[i];
    if (x.state === 'posted' || !formUsed(k, x)) continue;
    const chk = FORM_CFG[k].check(L, x);
    if ('error' in chk) continue;
    try {
      const res = await api.post<{ number?: string; assetCode?: string; paymentVoucherNo?: string | null; monthly?: number }>(chk.path, chk.body, companyId);
      const what = [res.number ?? res.assetCode, res.monthly != null ? `${money(res.monthly)}/month` : '', res.paymentVoucherNo ? `paid ${res.paymentVoucherNo}` : ''].filter(Boolean).join(' · ');
      st.set(st.get().map((y, j) => (j === i ? { ...y, state: 'posted', result: what } : y)));
      posted++;
    } catch (e) {
      st.set(st.get().map((y, j) => (j === i ? { ...y, state: 'error', result: e instanceof Error ? e.message : String(e) } : y)));
      failed++;
    }
  }
  const noun = FORM_CFG[k].noun;
  if (!posted && !failed) return { ok: false, msg: 'No ready rows — the Result column says what each row still needs.' };
  return { ok: failed === 0, msg: `Saved ${posted} ${noun}(s)${failed ? `, ${failed} failed — see the Result column` : ''}` };
}

function FormHeader({ k, ready, onPostAll }: { k: FormKind; ready: number; onPostAll: () => void }) {
  const st = forms[k];
  const noun = FORM_CFG[k].noun;
  return (
    <div className="entry-header">
      <span className="hint">{FORM_CFG[k].hint}</span>
      <span style={{ flex: 1 }} />
      <button className="btn ghost" onClick={() => st.set([...st.get(), ...Array.from({ length: 10 }, blankForm)])}>+ 10 rows</button>
      <button className="btn ghost" onClick={() => st.set([...st.get().filter((x) => x.state !== 'posted'), ...Array.from({ length: 3 }, blankForm)])}>Clear saved</button>
      <button className="btn ghost" onClick={() => st.reset()}>Clear all</button>
      <button className="btn primary" onClick={onPostAll}>Save {ready} ready {noun}{ready === 1 ? '' : 's'}</button>
    </div>
  );
}

// ================================================================ public facade used by App

export const ENTRY_SHEETS = ['invoice-entry', 'quote-entry', 'bill-entry', 'receipt-batch', 'payment-batch', 'expense-batch', 'asset-batch', 'prepay-batch'] as const;
export type EntryId = (typeof ENTRY_SHEETS)[number];
export const isEntrySheet = (id: string): id is EntryId => (ENTRY_SHEETS as readonly string[]).includes(id);
const isDoc = (id: EntryId): id is DocKind => id === 'invoice-entry' || id === 'quote-entry' || id === 'bill-entry';
const isForm = (id: EntryId): id is FormKind => id === 'asset-batch' || id === 'prepay-batch';

/** API list a batch sheet needs for its "against invoice/bill" column. */
export const openDocsPath = (id: string) => (id === 'receipt-batch' ? '/outstanding-invoices' : id === 'payment-batch' ? '/purchase-invoices' : undefined);
/** Normalise /outstanding-invoices or /purchase-invoices rows to OpenDoc. */
export function toOpenDocs(id: string, data: unknown): OpenDoc[] | undefined {
  if (!Array.isArray(data)) return undefined;
  if (id === 'receipt-batch') return data.map((o) => ({ id: o.invoiceId, partyCode: o.customerCode, no: o.invoiceNo, dueDate: o.dueDate, due: o.amountDue }));
  return data.filter((o) => o.balance > 0).map((o) => ({ id: o.invoiceId, partyCode: o.vendorCode, no: o.invoiceNo, dueDate: o.dueDate, due: o.balance }));
}

export function entryView(id: EntryId, L: Lookups | undefined, open: OpenDoc[] | undefined, today: string) {
  defaultDate = today;
  const loading = (msg: string) => ({ columns: [{ key: 'msg', label: 'Loading', width: 500 }] as Col[], rows: [{ msg, ...meta('muted') }] as Row[] });
  if (!L) return loading('Loading lists…');
  if (isDoc(id)) return docView(id, L);
  if (isForm(id)) return formView(id, L);
  if (id === 'expense-batch') return expView(L);
  if (!open) return loading('Loading open documents…');
  return settleView(id, L, open);
}

export async function postEntrySheet(id: EntryId, L: Lookups | undefined, open: OpenDoc[] | undefined, companyId: number, today: string, asDraft: boolean) {
  if (!L) return { ok: false, msg: 'Lists are still loading.' };
  if (isDoc(id)) return postDoc(id, L, companyId, today, asDraft);
  if (isForm(id)) return postForms(id, L, companyId);
  if (id !== 'expense-batch' && !open) return { ok: false, msg: 'Open documents are still loading.' };
  return postBatch(id, L, open ?? [], companyId);
}

export function EntryHeader({ id, L, today, ready, onPost }: { id: EntryId; L: Lookups | undefined; today: string; ready: number; onPost: (draft: boolean) => void }) {
  useEntryStores();
  if (isForm(id)) return <FormHeader k={id} ready={ready} onPostAll={() => onPost(false)} />;
  return isDoc(id) ? <DocHeader k={id} L={L} defaultDate={today} onPost={onPost} /> : <BatchHeader k={id} ready={ready} onPostAll={() => onPost(false)} />;
}
