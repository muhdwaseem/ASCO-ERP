// Entry dialogs in live mode: every save goes to the ASCO API, which calls C-ERP's own
// document services (same numbering, validation, VAT, allocation locks and GL posting as C-ERP).
import { useEffect, useMemo, useState } from 'react';
import { X } from 'lucide-react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api, vatFraction, type Lookups } from '../api/client';

export type LiveFormKind = 'sales-invoice' | 'purchase-invoice' | 'receipt' | 'payment' | 'expense' | 'customer' | 'vendor' | 'portal-access';

const TITLES: Record<LiveFormKind, string> = {
  'sales-invoice': 'New Sales Invoice', 'purchase-invoice': 'New Purchase Invoice', receipt: 'New Receipt Voucher',
  payment: 'New Payment Voucher', expense: 'New Expense', customer: 'New Customer', vendor: 'New Vendor', 'portal-access': 'Grant Employee Portal Access',
};
export const LIVE_TARGET: Record<LiveFormKind, string> = {
  'sales-invoice': 'sales-invoices', 'purchase-invoice': 'purchase-invoices', receipt: 'receipts', payment: 'payments',
  expense: 'expenses', customer: 'customers', vendor: 'vendors', 'portal-access': 'employees',
};

const today = () => new Date().toISOString().slice(0, 10);
const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
interface Line { itemId: string; description: string; accountId: string; qty: string; price: string; vat: string }
/** Pre-fill from a scanned bill (or any other source). */
export interface LivePrefill { partyId?: number; date?: string; reference?: string; lines?: { description: string; qty: string; price: string; vat: string }[] }

export function useLookups(companyId: number) {
  return useQuery({ queryKey: ['lookups', companyId], queryFn: () => api.get<Lookups>('/lookups', companyId), staleTime: 60_000 });
}

export function LiveDocForm({ kind, companyId, onClose, onDone, context, prefill }: { kind: LiveFormKind; companyId: number; onClose: () => void; onDone: (msg: string) => void; context?: { employeeId: number; name?: string }; prefill?: LivePrefill }) {
  const qc = useQueryClient();
  const lk = useLookups(companyId);
  const L = lk.data;
  const sales = kind === 'sales-invoice' || kind === 'receipt';
  const settlement = kind === 'receipt' || kind === 'payment';
  const master = kind === 'customer' || kind === 'vendor';
  const portal = kind === 'portal-access';
  const [login, setLogin] = useState({ email: '', password: '' });

  const [party, setParty] = useState(prefill?.partyId ? String(prefill.partyId) : '');
  const [date, setDate] = useState(prefill?.date ?? today());
  const [bank, setBank] = useState('');
  const [narration, setNarration] = useState('');
  const [reference, setReference] = useState(prefill?.reference ?? '');
  const [payLater, setPayLater] = useState(false);
  const [against, setAgainst] = useState('');
  const [amount, setAmount] = useState('');
  const [mode, setMode] = useState('BankTransfer');
  const [lines, setLines] = useState<Line[]>(prefill?.lines?.length
    ? prefill.lines.map((l) => ({ itemId: '', accountId: '', ...l }))
    : [{ itemId: '', description: '', accountId: '', qty: '1', price: '', vat: '0.05' }]);
  const [m, setM] = useState({ name: '', group: '', trn: '', email: '', phone: '', address: '', creditLimit: '0', terms: '30', currency: 'AED' });
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const esc = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);

  // sensible defaults once lookups arrive
  useEffect(() => {
    if (!L) return;
    if (!bank && L.bankAccounts[0]) setBank(String(L.bankAccounts[0].id));
    const open = L.openPeriods.find((p) => today() >= p.startDate && today() <= p.endDate) ?? L.openPeriods[L.openPeriods.length - 1];
    if (!prefill?.date && open && !(today() >= open.startDate && today() <= open.endDate)) setDate(open.endDate < today() ? open.endDate : open.startDate);
  }, [L]); // eslint-disable-line react-hooks/exhaustive-deps

  const parties = (sales ? L?.customers : L?.vendors) ?? [];
  const revenueAccounts = useMemo(() => L?.accounts.filter((a) => a.type === 'Income') ?? [], [L]);
  const costAccounts = useMemo(() => L?.accounts.filter((a) => a.type === 'Expense' || a.type === 'Asset') ?? [], [L]);
  const lineAccounts = kind === 'sales-invoice' ? revenueAccounts : costAccounts;

  const openDocs = useQuery({
    queryKey: ['open-docs', companyId, kind, party],
    queryFn: () => api.get<{ id: number; invoiceNo: string; outstanding: number; dueDate: string }[]>(`/${sales ? 'customers' : 'vendors'}/${party}/open-invoices`, companyId),
    enabled: settlement && !!party,
  });
  const chosenDoc = openDocs.data?.find((d) => String(d.id) === against);

  const totals = lines.reduce((t, l) => {
    const net = (Number(l.qty) || 0) * (Number(l.price) || 0);
    const vat = Math.round(net * Number(l.vat) * 100) / 100;
    return { net: t.net + net, vat: t.vat + vat, gross: t.gross + net + vat };
  }, { net: 0, vat: 0, gross: 0 });

  const setLine = (i: number, patch: Partial<Line>) => setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  const pickItem = (i: number, id: string) => {
    const it = L?.items.find((x) => String(x.id) === id);
    if (!it) return setLine(i, { itemId: '' });
    const acct = kind === 'sales-invoice' ? it.salesAccountId : it.purchaseAccountId;
    setLine(i, { itemId: id, description: it.name, price: String(kind === 'sales-invoice' ? it.sellingPrice : it.costPrice ?? ''), accountId: acct ? String(acct) : lines[i].accountId, vat: String(vatFraction(it.vatRate)) });
  };

  const submit = async () => {
    setError('');
    setBusy(true);
    try {
      const n = (v: string) => Number(v) || 0;
      let res: { number?: string; code?: string; name?: string } = {};
      if (portal) {
        if (!context) throw new Error('Select an employee row first.');
        if (login.password.length < 8) throw new Error('Password must be at least 8 characters.');
        const r = await api.post<{ userWasCreated: boolean; email: string }>(`/employees/${context.employeeId}/portal-access`, login, companyId);
        await qc.invalidateQueries();
        onDone(`Portal access ${r.userWasCreated ? 'created' : 'linked'} for ${r.email}`);
        return;
      }
      if (kind === 'customer' || kind === 'vendor') {
        if (!m.name.trim()) throw new Error('Name is required.');
        res = await api.post(`/${kind}s`, { name: m.name, group: m.group || null, currency: m.currency, creditLimit: n(m.creditLimit), paymentTermsDays: n(m.terms), trn: m.trn || null, email: m.email || null, phone: m.phone || null, address: m.address || null }, companyId);
      } else {
        if (!party && kind !== 'expense') throw new Error(`Choose a ${sales ? 'customer' : 'vendor'}.`);
        if (settlement) {
          const amt = n(amount) || chosenDoc?.outstanding || 0;
          if (amt <= 0) throw new Error('Enter an amount.');
          res = await api.post(kind === 'receipt' ? '/receipts' : '/vendor-payments', { partyId: n(party), invoiceId: against ? n(against) : null, date, bankAccountId: n(bank), amount: amt, narration: narration || null, paymentMode: mode, referenceNo: reference || null }, companyId);
        } else {
          const bad = lines.find((l) => !l.accountId);
          if (bad) throw new Error('Every line needs an account.');
          if (kind === 'sales-invoice') {
            res = await api.post('/sales-invoices', { customerId: n(party), date, narration: narration || null, lines: lines.map((l) => ({ description: l.description || 'Item', revenueAccountId: n(l.accountId), costCenterId: null, quantity: n(l.qty), unitPrice: n(l.price), vatRate: Number(l.vat), itemId: l.itemId ? n(l.itemId) : null })) }, companyId);
          } else if (kind === 'purchase-invoice') {
            res = await api.post('/purchase-invoices', { vendorId: n(party), vendorRef: reference || null, date, narration: narration || null, lines: lines.map((l) => ({ description: l.description || 'Item', expenseAccountId: n(l.accountId), costCenterId: null, quantity: n(l.qty), unitPrice: n(l.price), vatRate: Number(l.vat) })) }, companyId);
          } else {
            res = await api.post('/expenses', { vendorId: party ? n(party) : null, customerId: null, date, bankAccountId: payLater ? null : n(bank), reference: reference || null, narration: narration || 'Direct expense', payLater, lines: lines.map((l) => ({ expenseAccountId: n(l.accountId), costCenterId: null, description: l.description || null, amount: n(l.qty) * n(l.price), itemId: l.itemId ? n(l.itemId) : null, vatRate: Number(l.vat) })) }, companyId);
          }
        }
      }
      await qc.invalidateQueries();
      onDone(master ? `Created ${res.code ?? ''} ${res.name ?? ''}` : `Posted ${res.number} — GL voucher generated by C-ERP's posting engine`);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="modal-back" onMouseDown={onClose}>
      <div className="modal" onMouseDown={(e) => e.stopPropagation()}>
        <div className="modal-title"><span>{TITLES[kind]}</span><button className="icon-btn" onClick={onClose} aria-label="Close"><X size={16} /></button></div>
        <div className="modal-body">
          {portal ? (
            <>
              <p className="muted">{context?.name ?? 'The selected employee'} will be able to sign in and see only their own payslips, leave and salary advances.</p>
              <div className="form-row">
                <label>Login email <input type="email" value={login.email} onChange={(e) => setLogin({ ...login, email: e.target.value })} autoFocus /></label>
                <label>Initial password <input type="password" value={login.password} onChange={(e) => setLogin({ ...login, password: e.target.value })} /></label>
              </div>
            </>
          ) : !L ? <div className="muted">{lk.isError ? lk.error.message : 'Loading lists…'}</div> : master ? (
            <>
              <div className="form-row">
                <label>Name <input value={m.name} onChange={(e) => setM({ ...m, name: e.target.value })} autoFocus /></label>
                <label>Group <input value={m.group} onChange={(e) => setM({ ...m, group: e.target.value })} /></label>
              </div>
              <div className="form-row">
                <label>TRN <input value={m.trn} onChange={(e) => setM({ ...m, trn: e.target.value })} /></label>
                <label>Email <input type="email" value={m.email} onChange={(e) => setM({ ...m, email: e.target.value })} /></label>
                <label>Phone <input value={m.phone} onChange={(e) => setM({ ...m, phone: e.target.value })} /></label>
              </div>
              <div className="form-row">
                <label>Currency <input value={m.currency} onChange={(e) => setM({ ...m, currency: e.target.value.toUpperCase() })} /></label>
                <label>Credit limit <input inputMode="decimal" value={m.creditLimit} onChange={(e) => setM({ ...m, creditLimit: e.target.value })} /></label>
                <label>Payment terms (days) <input inputMode="numeric" value={m.terms} onChange={(e) => setM({ ...m, terms: e.target.value })} /></label>
              </div>
              <label className="full">Address <input value={m.address} onChange={(e) => setM({ ...m, address: e.target.value })} /></label>
            </>
          ) : (
            <>
              <div className="form-row">
                <label>{sales ? 'Customer' : 'Vendor'}
                  <select value={party} onChange={(e) => { setParty(e.target.value); setAgainst(''); }}>
                    <option value="">{kind === 'expense' ? '— none —' : '— choose —'}</option>
                    {parties.map((p) => <option key={p.id} value={p.id}>{p.code} · {p.name}</option>)}
                  </select>
                </label>
                <label>Date <input type="date" value={date} onChange={(e) => setDate(e.target.value)} /></label>
                {(settlement || kind === 'expense') && (
                  <label>{kind === 'receipt' ? 'Deposit to' : 'Paid from'}
                    <select value={bank} onChange={(e) => setBank(e.target.value)} disabled={payLater}>
                      {L.bankAccounts.map((b) => <option key={b.id} value={b.id}>{b.code} · {b.name} ({money(b.balance)})</option>)}
                    </select>
                  </label>
                )}
              </div>
              {settlement ? (
                <div className="form-row">
                  <label>Allocate against
                    <select value={against} onChange={(e) => setAgainst(e.target.value)}>
                      <option value="">On account / oldest first</option>
                      {(openDocs.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.invoiceNo} — due {d.dueDate} — outstanding {money(d.outstanding)}</option>)}
                    </select>
                  </label>
                  <label>Amount <input inputMode="decimal" value={amount} placeholder={chosenDoc ? money(chosenDoc.outstanding) : '0.00'} onChange={(e) => setAmount(e.target.value)} /></label>
                  <label>Mode
                    <select value={mode} onChange={(e) => setMode(e.target.value)}>
                      {['BankTransfer', 'Cash', 'Cheque', 'Card', 'PostDatedCheque', 'Other'].map((x) => <option key={x} value={x}>{x.replace(/([a-z])([A-Z])/g, '$1 $2')}</option>)}
                    </select>
                  </label>
                  <label>Reference <input value={reference} onChange={(e) => setReference(e.target.value)} /></label>
                </div>
              ) : (
                <>
                  <div className="form-row">
                    <label className="full">Narration <input value={narration} onChange={(e) => setNarration(e.target.value)} placeholder="Optional" /></label>
                    {kind !== 'sales-invoice' && <label>{kind === 'expense' ? 'Reference' : 'Vendor invoice no.'} <input value={reference} onChange={(e) => setReference(e.target.value)} /></label>}
                    {kind === 'expense' && <label className="check"><input type="checkbox" checked={payLater} onChange={(e) => setPayLater(e.target.checked)} /> Pay later</label>}
                  </div>
                  <table className="form-lines">
                    <thead><tr><th>Item</th><th>Description</th><th>{kind === 'sales-invoice' ? 'Revenue account' : 'Expense account'}</th><th>Qty</th><th>Rate</th><th>VAT</th><th>Amount</th><th /></tr></thead>
                    <tbody>
                      {lines.map((l, i) => (
                        <tr key={i}>
                          <td><select value={l.itemId} onChange={(e) => pickItem(i, e.target.value)}><option value="">—</option>{L.items.map((it) => <option key={it.id} value={it.id}>{it.code} · {it.name}</option>)}</select></td>
                          <td><input value={l.description} onChange={(e) => setLine(i, { description: e.target.value })} /></td>
                          <td><select value={l.accountId} onChange={(e) => setLine(i, { accountId: e.target.value })}><option value="">— choose —</option>{lineAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} · {a.name}</option>)}</select></td>
                          <td><input className="n" value={l.qty} onChange={(e) => setLine(i, { qty: e.target.value })} /></td>
                          <td><input className="n" value={l.price} onChange={(e) => setLine(i, { price: e.target.value })} /></td>
                          <td><select value={l.vat} onChange={(e) => setLine(i, { vat: e.target.value })}><option value="0.05">5%</option><option value="0">0%</option></select></td>
                          <td className="n">{money((Number(l.qty) || 0) * (Number(l.price) || 0))}</td>
                          <td>{lines.length > 1 && <button className="icon-btn" onClick={() => setLines(lines.filter((_, j) => j !== i))} aria-label="Remove line"><X size={14} /></button>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  <button className="btn ghost" onClick={() => setLines([...lines, { itemId: '', description: '', accountId: lines[lines.length - 1].accountId, qty: '1', price: '', vat: '0.05' }])}>+ Add line</button>
                  <div className="totals"><span>Net <b>{money(totals.net)}</b></span><span>VAT <b>{money(totals.vat)}</b></span><span>Total <b>{money(totals.gross)}</b></span></div>
                </>
              )}
            </>
          )}
          {error && <div className="form-error">{error}</div>}
        </div>
        <div className="modal-foot">
          <span className="muted">{master ? 'Saved to the live company.' : 'Posted by C-ERP’s engine: document + balanced GL voucher, one number.'}</span>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={submit} disabled={busy || (!L && !portal)}>{busy ? 'Saving…' : master || portal ? 'Save' : 'Save & Post'}</button>
        </div>
      </div>
    </div>
  );
}
