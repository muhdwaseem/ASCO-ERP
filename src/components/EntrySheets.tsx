// Excel-style data entry for live companies: type documents straight into the grid.
//  • Invoice Entry — header strip (customer, date, narration) + line cells; Ctrl+Enter posts.
//  • Receipt Batch — one receipt per row; "Post all" posts every ready row and reports per row.
// Cells that need a customer / item / account / invoice offer search-as-you-type (Grid optionsFor).
// Everything posts through the ASCO API → C-ERP's own services, same as the popup forms.
import { useState, useSyncExternalStore } from 'react';
import type { Col, Row, RowMeta } from '../modules/registry';
import { api, vatFraction, type Lookups } from '../api/client';

// ---------------------------------------------------------------- tiny store + helpers

function createStore<T>(init: () => T) {
  let value = init();
  let ver = 0;
  const subs = new Set<() => void>();
  const set = (v: T) => { value = v; ver++; subs.forEach((f) => f()); };
  return {
    get: () => value,
    set,
    reset: () => set(init()),
    useValue: () => { useSyncExternalStore((cb) => { subs.add(cb); return () => subs.delete(cb); }, () => ver); return value; },
  };
}

const num = (v: string) => (v.trim() === '' ? 0 : Number(v.replace(/,/g, '')) || 0);
const r2 = (n: number) => Math.round(n * 100) / 100;
const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const meta = (style?: RowMeta['style']) => ({ _meta: { style } as RowMeta });
const t = (key: string, label: string, width = 120): Col => ({ key, label, width });
const m = (key: string, label: string, width = 110): Col => ({ key, label, width, type: 'money' });
const n = (key: string, label: string, width = 70): Col => ({ key, label, width, type: 'number' });

type Customer = Lookups['customers'][number];
type Item = Lookups['items'][number];
type Account = Lookups['accounts'][number];
type Bank = Lookups['bankAccounts'][number];

const custLabel = (c: Customer) => `${c.code} · ${c.name}`;
const itemLabel = (i: Item) => `${i.code} · ${i.name}`;
const acctLabel = (a: { code: string; name: string }) => `${a.code} · ${a.name}`;

/** Resolve typed text to a record: exact label, then code, then a unique "contains" match. */
function resolve<T>(list: T[], label: (x: T) => string, code: (x: T) => string, text: string): T | undefined {
  const q = text.trim().toLowerCase();
  if (!q) return undefined;
  return list.find((x) => label(x).toLowerCase() === q)
    ?? list.find((x) => code(x).toLowerCase() === q.split(' ')[0])
    ?? (() => { const hits = list.filter((x) => label(x).toLowerCase().includes(q)); return hits.length === 1 ? hits[0] : undefined; })();
}

/** Small search box with a suggestion list — used in the header strips. */
function Combo({ value, options, placeholder, onChange }: { value: string; options: string[]; placeholder?: string; onChange: (v: string) => void }) {
  const [open, setOpen] = useState(false);
  const [hi, setHi] = useState(0);
  const matches = options.filter((o) => o.toLowerCase().includes(value.trim().toLowerCase())).slice(0, 8);
  return (
    <span className="combo" style={{ position: 'relative', display: 'block' }}>
      <input value={value} placeholder={placeholder} onFocus={() => setOpen(true)} onBlur={() => setTimeout(() => setOpen(false), 120)}
        onChange={(e) => { onChange(e.target.value); setOpen(true); setHi(0); }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') { e.preventDefault(); setHi((h) => Math.min(matches.length - 1, h + 1)); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setHi((h) => Math.max(0, h - 1)); }
          else if ((e.key === 'Enter' || e.key === 'Tab') && open && matches.length && !options.includes(value)) { onChange(matches[hi] ?? matches[0]); setOpen(false); }
        }} />
      {open && matches.length > 0 && !options.includes(value) && (
        <div className="cell-suggest" onMouseDown={(e) => e.preventDefault()}>
          {matches.map((o, i) => <div key={o} className={i === hi ? 'on' : ''} onMouseDown={() => { onChange(o); setOpen(false); }}>{o}</div>)}
        </div>
      )}
    </span>
  );
}

// ================================================================ INVOICE ENTRY

interface InvLine { item: string; description: string; account: string; qty: string; rate: string; vat: string }
interface InvDraft { customer: string; date: string; narration: string; lines: InvLine[] }
const INV_LINES = 12;
const blankLine = (): InvLine => ({ item: '', description: '', account: '', qty: '', rate: '', vat: '' });
export const invoiceDraft = createStore<InvDraft>(() => ({ customer: '', date: '', narration: '', lines: Array.from({ length: INV_LINES }, blankLine) }));

export const INVOICE_COLUMNS: Col[] = [
  t('item', 'Item (type to search)', 210), t('description', 'Description', 240), t('account', 'Revenue Account', 220),
  n('qty', 'Qty', 70), m('rate', 'Rate', 100), t('vat', 'VAT', 56), m('net', 'Net', 110), m('vatAmt', 'VAT Amt', 90), m('total', 'Total', 110),
];
const INV_KEYS: (keyof InvLine)[] = ['item', 'description', 'account', 'qty', 'rate', 'vat'];
const vatOf = (v: string) => (v.trim() === '' ? 0.05 : v.startsWith('0') ? 0 : 0.05);
const hasContent = (l: InvLine) => !!(l.item || l.description || l.account || l.qty || l.rate);
const revenueAccounts = (L: Lookups) => L.accounts.filter((a) => a.type === 'Income');

function lineProblem(L: Lookups, l: InvLine): string | null {
  if (l.item && !resolve(L.items, itemLabel, (i) => i.code, l.item)) return `item "${l.item}" not found`;
  if (!resolve(revenueAccounts(L), acctLabel, (a) => a.code, l.account)) return 'choose a revenue account';
  if (num(l.qty) <= 0) return 'enter a quantity';
  if (l.rate.trim() === '') return 'enter a rate';
  return null;
}

export function invoiceView(L: Lookups | undefined, d: InvDraft) {
  if (!L) return { columns: INVOICE_COLUMNS, rows: [{ item: 'Loading lists…', ...meta('muted') }] as Row[] };
  let net = 0, vat = 0;
  const problems: string[] = [];
  const rows: Row[] = d.lines.map((l, i) => {
    if (!hasContent(l)) return { item: '', description: '', account: '', vat: '' };
    const lnNet = r2(num(l.qty) * num(l.rate));
    const lnVat = r2(lnNet * vatOf(l.vat));
    net += lnNet; vat += lnVat;
    const p = lineProblem(L, l);
    if (p) problems.push(`Line ${i + 1}: ${p}`);
    return {
      item: l.item, description: l.description, account: l.account, qty: l.qty === '' ? undefined : num(l.qty), rate: l.rate === '' ? undefined : num(l.rate),
      vat: l.vat || '5%', net: lnNet, vatAmt: lnVat, total: r2(lnNet + lnVat), ...(p ? meta('warn') : {}),
    };
  });
  const customerOk = !!resolve(L.customers, custLabel, (c) => c.code, d.customer);
  const status = !d.customer ? 'Choose the customer in the strip above'
    : !customerOk ? `Customer "${d.customer}" not found`
    : problems[0] ?? (net > 0 ? 'Ready — press Ctrl+Enter (or Post) to post the invoice' : 'Type the invoice lines');
  rows.push({ description: 'Total', net: r2(net), vatAmt: r2(vat), total: r2(net + vat), _meta: { style: 'grand', formula: {} } });
  rows.push({ description: status, ...meta(problems.length || (d.customer && !customerOk) ? 'warn' : 'muted') });
  return {
    columns: INVOICE_COLUMNS, rows,
    editable: (r: number, c: number) => r < INV_LINES && c < INV_KEYS.length,
    onEdit: (r: number, c: number, value: string) => invoiceEdit(L, r, c, value),
    optionsFor: (r: number, c: number) => (r >= INV_LINES ? undefined
      : c === 0 ? L.items.map(itemLabel) : c === 2 ? revenueAccounts(L).map(acctLabel) : c === 5 ? ['5%', '0%'] : undefined),
  };
}

function invoiceEdit(L: Lookups, r: number, c: number, value: string) {
  const d = invoiceDraft.get();
  const key = INV_KEYS[c];
  if (!key || r >= INV_LINES) return;
  const line = { ...d.lines[r], [key]: key === 'qty' || key === 'rate' ? value.replace(/,/g, '') : value };
  if (key === 'vat') line.vat = value.trim() === '' ? '' : value.trim().startsWith('0') ? '0%' : '5%';
  if (key === 'item') {
    const it = resolve(L.items, itemLabel, (i) => i.code, value);
    if (it) {
      line.item = itemLabel(it);
      line.description = line.description || it.name;
      if (it.sellingPrice) line.rate = String(it.sellingPrice);
      const acct = L.accounts.find((a) => a.id === it.salesAccountId);
      if (acct && !line.account) line.account = acctLabel(acct);
      line.vat = vatFraction(it.vatRate) === 0 ? '0%' : '5%';
      if (!line.qty) line.qty = '1';
    }
  }
  if (key === 'account') {
    const a = resolve(revenueAccounts(L), acctLabel, (x) => x.code, value);
    if (a) line.account = acctLabel(a);
  }
  invoiceDraft.set({ ...d, lines: d.lines.map((l, i) => (i === r ? line : l)) });
}

export async function postInvoice(L: Lookups | undefined, companyId: number, defaultDate: string, asDraft: boolean): Promise<{ ok: boolean; msg: string }> {
  if (!L) return { ok: false, msg: 'Lists are still loading.' };
  const d = invoiceDraft.get();
  const cust = resolve(L.customers, custLabel, (c) => c.code, d.customer);
  if (!cust) return { ok: false, msg: d.customer ? `Customer "${d.customer}" not found.` : 'Choose the customer first.' };
  const used = d.lines.map((l, i) => ({ l, i })).filter(({ l }) => hasContent(l));
  if (!used.length) return { ok: false, msg: 'Type at least one invoice line.' };
  for (const { l, i } of used) { const p = lineProblem(L, l); if (p) return { ok: false, msg: `Line ${i + 1}: ${p}.` }; }
  try {
    const res = await api.post<{ number: string }>('/sales-invoices', {
      customerId: cust.id, date: d.date || defaultDate, narration: d.narration || null, draft: asDraft,
      lines: used.map(({ l }) => {
        const it = l.item ? resolve(L.items, itemLabel, (x) => x.code, l.item) : undefined;
        return {
          description: l.description || it?.name || 'Item', revenueAccountId: resolve(revenueAccounts(L), acctLabel, (a) => a.code, l.account)!.id,
          costCenterId: null, quantity: num(l.qty), unitPrice: num(l.rate), vatRate: vatOf(l.vat), itemId: it?.id ?? null,
        };
      }),
    }, companyId);
    invoiceDraft.reset();
    return { ok: true, msg: `${asDraft ? 'Saved draft' : 'Posted'} ${res.number} for ${cust.name}${asDraft ? ' — submit/approve/post it from the Sales Invoice sheet' : ' — GL voucher posted by C-ERP'}` };
  } catch (e) {
    return { ok: false, msg: e instanceof Error ? e.message : String(e) };
  }
}

export function InvoiceEntryHeader({ L, defaultDate, onPost }: { L: Lookups | undefined; defaultDate: string; onPost: (draft: boolean) => void }) {
  const d = invoiceDraft.useValue();
  const cust = L ? resolve(L.customers, custLabel, (c) => c.code, d.customer) : undefined;
  const date = d.date || defaultDate;
  const due = cust ? new Date(Date.parse(date) + cust.paymentTermsDays * 86_400_000).toISOString().slice(0, 10) : '';
  return (
    <div className="entry-header">
      <label style={{ minWidth: 280 }}>Customer
        <Combo value={d.customer} placeholder="Type name or code…" options={L?.customers.map(custLabel) ?? []} onChange={(v) => invoiceDraft.set({ ...invoiceDraft.get(), customer: v })} />
        {cust && <span className="picked">{cust.paymentTermsDays}-day terms · due {due}</span>}
      </label>
      <label>Date <input type="date" value={date} onChange={(e) => invoiceDraft.set({ ...invoiceDraft.get(), date: e.target.value })} /></label>
      <label className="grow">Narration <input value={d.narration} placeholder="Optional" onChange={(e) => invoiceDraft.set({ ...invoiceDraft.get(), narration: e.target.value })} /></label>
      <span className="hint">Type in the cells · Tab moves · Ctrl+Enter posts</span>
      <button className="btn ghost" onClick={() => invoiceDraft.reset()}>Clear</button>
      <button className="btn ghost" onClick={() => onPost(true)}>Save draft</button>
      <button className="btn primary" onClick={() => onPost(false)}>Post invoice</button>
    </div>
  );
}

// ================================================================ RECEIPT BATCH

interface RcptRow { customer: string; date: string; invoice: string; amount: string; mode: string; reference: string; bank: string; result?: string; state?: 'posted' | 'error' }
const blankRcpt = (): RcptRow => ({ customer: '', date: '', invoice: '', amount: '', mode: '', reference: '', bank: '' });
export const receiptBatch = createStore<RcptRow[]>(() => Array.from({ length: 15 }, blankRcpt));

export interface OpenInvoice { invoiceId: number; customerCode: string; customerName: string; invoiceNo: string; dueDate: string; amountDue: number }
const MODES = ['BankTransfer', 'Cash', 'Cheque', 'Card', 'PostDatedCheque', 'Other'];
const invLabel = (o: OpenInvoice) => `${o.invoiceNo} · due ${o.dueDate} · ${money(o.amountDue)}`;

export const RECEIPT_COLUMNS: Col[] = [
  t('customer', 'Customer (type to search)', 230), t('date', 'Date', 100), t('invoice', 'Against Invoice', 260), m('outstanding', 'Outstanding', 110),
  m('amount', 'Amount', 110), t('mode', 'Mode', 120), t('reference', 'Reference / Cheque', 140), t('bank', 'Deposit To', 210), t('result', 'Result', 280),
];
const R_KEYS: (keyof RcptRow | null)[] = ['customer', 'date', 'invoice', null, 'amount', 'mode', 'reference', 'bank', null];
const rowEmpty = (x: RcptRow) => !x.customer && !x.amount && !x.invoice;

function rcptCheck(L: Lookups, open: OpenInvoice[], x: RcptRow) {
  const cust = resolve(L.customers, custLabel, (c) => c.code, x.customer);
  if (!cust) return { error: x.customer ? `customer "${x.customer}" not found` : 'choose a customer' };
  const inv = x.invoice ? open.find((o) => invLabel(o) === x.invoice || o.invoiceNo.toLowerCase() === x.invoice.trim().toLowerCase().split(' ')[0]) : undefined;
  if (x.invoice && !inv) return { error: `invoice "${x.invoice}" isn't open for this customer` };
  if (!/^\d{4}-\d{2}-\d{2}$/.test(x.date)) return { error: 'date must be YYYY-MM-DD' };
  if (num(x.amount) <= 0) return { error: 'enter the amount' };
  const bank = resolve(L.bankAccounts, acctLabel, (b) => b.code, x.bank);
  if (!bank) return { error: 'choose the bank/cash account' };
  if (!MODES.includes(x.mode)) return { error: 'choose a payment mode' };
  return { cust, inv, bank };
}

export function receiptView(L: Lookups | undefined, open: OpenInvoice[] | undefined, rowsIn: RcptRow[]) {
  if (!L || !open) return { columns: RECEIPT_COLUMNS, rows: [{ customer: 'Loading customers and open invoices…', ...meta('muted') }] as Row[] };
  let total = 0, ready = 0;
  const rows: Row[] = rowsIn.map((x) => {
    if (rowEmpty(x)) return { customer: '', date: '', invoice: '', mode: '', reference: '', bank: '', result: '' };
    const chk = rcptCheck(L, open, x);
    const inv = 'inv' in chk ? chk.inv : undefined;
    const result = x.state === 'posted' ? `✓ Posted ${x.result}` : x.state === 'error' ? `⚠ ${x.result}` : 'error' in chk ? `… ${chk.error}` : 'Ready';
    if (x.state === 'posted' || !('error' in chk)) total += num(x.amount);
    if (!x.state && !('error' in chk)) ready++;
    return {
      customer: x.customer, date: x.date, invoice: x.invoice, outstanding: inv?.amountDue, amount: x.amount === '' ? undefined : num(x.amount),
      mode: x.mode.replace(/([a-z])([A-Z])/g, '$1 $2'), reference: x.reference, bank: x.bank, result,
      ...(x.state === 'posted' ? meta('muted') : x.state === 'error' ? meta('warn') : {}),
    };
  });
  rows.push({ invoice: `Total (${ready} ready to post)`, amount: r2(total), _meta: { style: 'grand', formula: {} } });
  return {
    columns: RECEIPT_COLUMNS, rows, ready,
    editable: (r: number, c: number) => r < rowsIn.length && R_KEYS[c] != null && rowsIn[r].state !== 'posted',
    onEdit: (r: number, c: number, value: string) => receiptEdit(L, open, r, c, value),
    optionsFor: (r: number, c: number) => {
      if (r >= rowsIn.length) return undefined;
      if (c === 0) return L.customers.map(custLabel);
      if (c === 2) {
        const cust = resolve(L.customers, custLabel, (x) => x.code, rowsIn[r].customer);
        return open.filter((o) => !cust || o.customerCode === cust.code).map(invLabel);
      }
      if (c === 5) return MODES.map((x) => x.replace(/([a-z])([A-Z])/g, '$1 $2'));
      if (c === 7) return L.bankAccounts.map(acctLabel);
      return undefined;
    },
  };
}

let defaultReceiptDate = '';
export const setReceiptDefaultDate = (d: string) => { defaultReceiptDate = d; };

function receiptEdit(L: Lookups, open: OpenInvoice[], r: number, c: number, value: string) {
  const rows = receiptBatch.get();
  const key = R_KEYS[c];
  if (!key) return;
  const x: RcptRow = { ...rows[r], [key]: key === 'amount' ? value.replace(/,/g, '') : value, state: undefined, result: undefined };
  if (key === 'customer') {
    const cust = resolve(L.customers, custLabel, (cc) => cc.code, value);
    if (cust) x.customer = custLabel(cust);
    if (!x.date) x.date = defaultReceiptDate;
    if (!x.mode) x.mode = 'BankTransfer';
    if (!x.bank && L.bankAccounts[0]) x.bank = acctLabel(L.bankAccounts[0] as Bank);
  }
  if (key === 'mode') x.mode = MODES.find((md) => md.toLowerCase() === value.replace(/\s/g, '').toLowerCase()) ?? value;
  if (key === 'bank') { const b = resolve(L.bankAccounts, acctLabel, (bb) => bb.code, value); if (b) x.bank = acctLabel(b); }
  if (key === 'invoice') {
    const inv = open.find((o) => invLabel(o) === value || o.invoiceNo.toLowerCase() === value.trim().toLowerCase().split(' ')[0]);
    if (inv) { x.invoice = invLabel(inv); if (!x.amount) x.amount = String(inv.amountDue); }
  }
  receiptBatch.set(rows.map((y, i) => (i === r ? x : y)));
}

/** Posts every ready row in order; each row records its own receipt number or error. */
export async function postReceiptBatch(L: Lookups | undefined, open: OpenInvoice[] | undefined, companyId: number): Promise<{ ok: boolean; msg: string }> {
  if (!L || !open) return { ok: false, msg: 'Lists are still loading.' };
  let posted = 0, failed = 0;
  const rows = receiptBatch.get();
  for (let i = 0; i < rows.length; i++) {
    const x = receiptBatch.get()[i];
    if (rowEmpty(x) || x.state === 'posted') continue;
    const chk = rcptCheck(L, open, x);
    if ('error' in chk) continue; // not ready — left as is, the Result column says why
    try {
      const res = await api.post<{ number: string }>('/receipts', {
        partyId: chk.cust!.id, invoiceId: chk.inv?.invoiceId ?? null, date: x.date, bankAccountId: chk.bank!.id, amount: num(x.amount),
        narration: null, paymentMode: x.mode, referenceNo: x.reference || null,
      }, companyId);
      receiptBatch.set(receiptBatch.get().map((y, j) => (j === i ? { ...y, state: 'posted', result: res.number } : y)));
      posted++;
    } catch (e) {
      receiptBatch.set(receiptBatch.get().map((y, j) => (j === i ? { ...y, state: 'error', result: e instanceof Error ? e.message : String(e) } : y)));
      failed++;
    }
  }
  if (!posted && !failed) return { ok: false, msg: 'No ready rows — fill customer, date, amount, mode and bank (the Result column says what\'s missing).' };
  return { ok: failed === 0, msg: `Posted ${posted} receipt(s)${failed ? `, ${failed} failed — see the Result column` : ''}` };
}

export function ReceiptBatchHeader({ ready, onPostAll }: { ready: number; onPostAll: () => void }) {
  return (
    <div className="entry-header">
      <span className="hint">One receipt per row · type a customer, pick its invoice (amount fills in) · Tab moves · Ctrl+Enter posts all ready rows</span>
      <span style={{ flex: 1 }} />
      <button className="btn ghost" onClick={() => receiptBatch.set([...receiptBatch.get(), ...Array.from({ length: 10 }, blankRcpt)])}>+ 10 rows</button>
      <button className="btn ghost" onClick={() => receiptBatch.set(receiptBatch.get().filter((x) => x.state !== 'posted').concat(Array.from({ length: 3 }, blankRcpt)))}>Clear posted</button>
      <button className="btn ghost" onClick={() => receiptBatch.reset()}>Clear all</button>
      <button className="btn primary" onClick={onPostAll}>Post {ready} ready receipt{ready === 1 ? '' : 's'}</button>
    </div>
  );
}

export type { Account, Customer, Item };
