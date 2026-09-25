// Excel-style dialog for the five core documents. Every submit goes through the posting engine.
import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import type { DocLine } from '../engine/types';
import type { FormKind } from '../modules/registry';
import { store } from '../engine/store';
import {
  ACC, billOutstanding, docTotals, invoiceOutstanding, PostingError, postExpense, postPayment, postPurchaseInvoice, postReceipt, postSalesInvoice,
} from '../engine/ledger';

const TITLES: Record<FormKind, string> = {
  'sales-invoice': 'New Sales Invoice', 'purchase-invoice': 'New Purchase Invoice', receipt: 'New Receipt Voucher', payment: 'New Payment Voucher', expense: 'New Expense',
};

const today = () => new Date().toISOString().slice(0, 10);
const addDays = (d: string, n: number) => new Date(Date.parse(d) + n * 86_400_000).toISOString().slice(0, 10);

interface Line { ref: string; description: string; qty: string; price: string; vat: string }

export function DocForm({ kind, user, onClose, onDone }: { kind: FormKind; user: string; onClose: () => void; onDone: (msg: string) => void }) {
  const s = store.get();
  const sales = kind === 'sales-invoice' || kind === 'receipt';
  const parties = sales ? s.customers : s.vendors;
  const banks = s.accounts.filter((a) => a.parent === '1260');
  const expenseAccounts = s.accounts.filter((a) => a.isPostable && a.type === 'Expense');

  const [party, setParty] = useState(parties[0]?.code ?? '');
  const [date, setDate] = useState(today());
  const [bank, setBank] = useState(ACC.BANK as string);
  const [narration, setNarration] = useState('');
  const [error, setError] = useState('');
  const [lines, setLines] = useState<Line[]>(() =>
    kind === 'sales-invoice'
      ? [{ ref: s.items[0].code, description: s.items[0].name, qty: '1', price: String(s.items[0].sellingPrice), vat: '0.05' }]
      : [{ ref: ACC.MISC, description: '', qty: '1', price: '', vat: '0.05' }],
  );

  const openDocs = (sales ? s.salesInvoices : s.purchaseInvoices)
    .filter((d) => d.party === party)
    .map((d) => ({ no: d.no, out: sales ? invoiceOutstanding(s, d.no) : billOutstanding(s, d.no) }))
    .filter((d) => d.out > 0);
  const [against, setAgainst] = useState('');
  const [amount, setAmount] = useState('');
  const effectiveAgainst = openDocs.some((d) => d.no === against) ? against : '';

  const docLines: DocLine[] = lines.map((l) => {
    const it = s.items.find((i) => i.code === l.ref);
    return {
      description: l.description || it?.name || '',
      account: kind === 'sales-invoice' ? it?.salesAccount ?? ACC.REV : l.ref,
      quantity: Number(l.qty) || 0, unitPrice: Number(l.price) || 0, vatRate: Number(l.vat),
      costCenter: it?.category === 'Audit' ? 'CC-AUD' : it?.category === 'PRO Services' ? 'CC-PRO' : 'CC-ACC',
    };
  });
  const totals = docTotals(docLines);
  const settlement = kind === 'receipt' || kind === 'payment';

  useEffect(() => {
    const esc = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);

  const updateLine = (i: number, patch: Partial<Line>) => setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));

  const submit = () => {
    setError('');
    try {
      const terms = parties.find((p) => p.code === party)?.paymentTermsDays ?? 30;
      let no = '';
      store.mutate((st) => {
        if (kind === 'sales-invoice') no = postSalesInvoice(st, { party, date, dueDate: addDays(date, terms), lines: docLines, narration: narration || undefined, agent: st.customers.find((c) => c.code === party)?.salesperson }, user);
        else if (kind === 'purchase-invoice') no = postPurchaseInvoice(st, { party, date, dueDate: addDays(date, terms), lines: docLines, narration: narration || undefined }, user);
        else if (kind === 'expense') no = postExpense(st, { vendor: party || undefined, date, bankAccount: bank, narration: narration || 'Direct expense', lines: docLines }, user);
        else {
          const amt = Number(amount) || (effectiveAgainst ? openDocs.find((d) => d.no === effectiveAgainst)!.out : 0);
          if (amt <= 0) throw new PostingError('Enter an amount.');
          const payload = { party, againstDoc: effectiveAgainst || undefined, date, bankAccount: bank, amount: amt, paymentMode: 'Bank Transfer' as const };
          no = kind === 'receipt' ? postReceipt(st, payload, user) : postPayment(st, payload, user);
        }
      });
      onDone(`Posted ${no} — voucher auto-generated in the General Ledger`);
    } catch (e) {
      setError(e instanceof PostingError ? e.message : String(e));
    }
  };

  return (
    <div className="modal-back" onMouseDown={onClose}>
      <div className="modal" onMouseDown={(e) => e.stopPropagation()}>
        <div className="modal-title">
          <span>{TITLES[kind]}</span>
          <button className="icon-btn" onClick={onClose} aria-label="Close"><X size={16} /></button>
        </div>
        <div className="modal-body">
          <div className="form-row">
            <label>{sales ? 'Customer' : 'Vendor'}
              <select value={party} onChange={(e) => { setParty(e.target.value); setAgainst(''); }}>
                {kind === 'expense' && <option value="">— none —</option>}
                {parties.map((p) => <option key={p.code} value={p.code}>{p.code} · {p.name}</option>)}
              </select>
            </label>
            <label>Date <input type="date" value={date} onChange={(e) => setDate(e.target.value)} /></label>
            {(settlement || kind === 'expense') && (
              <label>{kind === 'receipt' ? 'Deposit to' : 'Paid from'}
                <select value={bank} onChange={(e) => setBank(e.target.value)}>
                  {banks.map((b) => <option key={b.code} value={b.code}>{b.code} · {b.name}</option>)}
                </select>
              </label>
            )}
          </div>

          {settlement ? (
            <div className="form-row">
              <label>Allocate against
                <select value={effectiveAgainst} onChange={(e) => setAgainst(e.target.value)}>
                  <option value="">On account (unallocated)</option>
                  {openDocs.map((d) => <option key={d.no} value={d.no}>{d.no} — outstanding {d.out.toFixed(2)}</option>)}
                </select>
              </label>
              <label>Amount (AED)
                <input inputMode="decimal" value={amount} placeholder={effectiveAgainst ? openDocs.find((d) => d.no === effectiveAgainst)?.out.toFixed(2) : '0.00'} onChange={(e) => setAmount(e.target.value)} />
              </label>
            </div>
          ) : (
            <>
              <label className="full">Narration <input value={narration} onChange={(e) => setNarration(e.target.value)} placeholder="Optional" /></label>
              <table className="form-lines">
                <thead><tr><th>{kind === 'sales-invoice' ? 'Item' : 'Expense account'}</th><th>Description</th><th>Qty</th><th>Rate</th><th>VAT</th><th>Amount</th><th /></tr></thead>
                <tbody>
                  {lines.map((l, i) => (
                    <tr key={i}>
                      <td>
                        <select value={l.ref} onChange={(e) => {
                          const it = s.items.find((x) => x.code === e.target.value);
                          updateLine(i, kind === 'sales-invoice' && it ? { ref: it.code, description: it.name, price: String(it.sellingPrice) } : { ref: e.target.value });
                        }}>
                          {kind === 'sales-invoice'
                            ? s.items.map((it) => <option key={it.code} value={it.code}>{it.code} · {it.name}</option>)
                            : expenseAccounts.map((a) => <option key={a.code} value={a.code}>{a.code} · {a.name}</option>)}
                        </select>
                      </td>
                      <td><input value={l.description} onChange={(e) => updateLine(i, { description: e.target.value })} /></td>
                      <td><input className="n" value={l.qty} onChange={(e) => updateLine(i, { qty: e.target.value })} /></td>
                      <td><input className="n" value={l.price} onChange={(e) => updateLine(i, { price: e.target.value })} /></td>
                      <td>
                        <select value={l.vat} onChange={(e) => updateLine(i, { vat: e.target.value })}>
                          <option value="0.05">SR 5%</option><option value="0">ZR 0%</option>
                        </select>
                      </td>
                      <td className="n">{((Number(l.qty) || 0) * (Number(l.price) || 0)).toFixed(2)}</td>
                      <td>{lines.length > 1 && <button className="icon-btn" onClick={() => setLines(lines.filter((_, j) => j !== i))} aria-label="Remove line"><X size={14} /></button>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <button className="btn ghost" onClick={() => setLines([...lines, kind === 'sales-invoice' ? { ref: s.items[0].code, description: s.items[0].name, qty: '1', price: String(s.items[0].sellingPrice), vat: '0.05' } : { ref: ACC.MISC, description: '', qty: '1', price: '', vat: '0.05' }])}>+ Add line</button>
              <div className="totals">
                <span>Net <b>{totals.net.toFixed(2)}</b></span><span>VAT <b>{totals.vat.toFixed(2)}</b></span><span>Total <b>{totals.gross.toFixed(2)} AED</b></span>
              </div>
            </>
          )}
          {error && <div className="form-error">{error}</div>}
        </div>
        <div className="modal-foot">
          <span className="muted">Posts atomically: document + balanced GL voucher share one number.</span>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={submit}>Save &amp; Post</button>
        </div>
      </div>
    </div>
  );
}
