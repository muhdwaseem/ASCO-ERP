// Ask AI — Phase 4 wires this to Claude (tool-use over the same read models).
// Today it answers a handful of intents deterministically from the ledger so the UX is real.
import { useState } from 'react';
import { Sparkles, Send } from 'lucide-react';
import { useLedger } from '../engine/store';
import { aging, balances, naturalBalance, pnl, ACC, r2, partyName, invoiceOutstanding } from '../engine/ledger';
import { FY_START, TODAY } from '../modules/registry';
import type { LedgerState } from '../engine/types';

const SUGGESTIONS = ['What is my net profit this year?', 'Who owes me the most?', 'How much cash do I have?', 'What is my VAT payable?', 'Which invoices are overdue?'];
const f = (n: number) => `AED ${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

function answer(s: LedgerState, q: string): string {
  const x = q.toLowerCase();
  const b = balances(s);
  const bal = (c: string) => (b.has(c) ? naturalBalance(s, c, b.get(c)!) : 0);
  if (/profit|loss|margin|earn/.test(x)) {
    const p = pnl(s, FY_START, TODAY);
    return `Year to date revenue is ${f(p.totalRev)} against expenses of ${f(p.totalExp)}, so net profit is ${f(p.net)} (${p.totalRev ? ((p.net / p.totalRev) * 100).toFixed(1) : 0}% margin). Biggest expense: ${[...p.expenses].sort((a, c) => c.amount - a.amount)[0]?.name ?? '—'}.`;
  }
  if (/owe|debtor|receivable|customer/.test(x)) {
    const top = aging(s, 'AR', TODAY).sort((a, c) => c.total - a.total);
    return top.length ? `${top[0].name} owes the most: ${f(top[0].total)}. Total receivables: ${f(r2(top.reduce((a, r) => a + r.total, 0)))} across ${top.length} customers.` : 'No customer owes you anything right now.';
  }
  if (/cash|bank|liquid/.test(x)) return `Cash & bank stands at ${f(r2(bal(ACC.BANK) + bal(ACC.PETTY)))} (Emirates Bank ${f(bal(ACC.BANK))}, petty cash ${f(bal(ACC.PETTY))}).`;
  if (/vat|tax/.test(x)) return `Output VAT ${f(bal(ACC.VAT_OUT))} − input VAT ${f(bal(ACC.VAT_IN))} = net VAT payable ${f(r2(bal(ACC.VAT_OUT) - bal(ACC.VAT_IN)))}.`;
  if (/overdue|late|chase/.test(x)) {
    const od = s.salesInvoices.filter((i) => i.dueDate < TODAY && invoiceOutstanding(s, i.no) > 0);
    return od.length ? `${od.length} overdue: ${od.map((i) => `${i.no} (${partyName(s, i.party)}, ${f(invoiceOutstanding(s, i.no))})`).join('; ')}.` : 'Nothing is overdue.';
  }
  return "I can't answer that offline yet. In Phase 4 this box calls Claude with read-only tools over your ledger (trial balance, aging, GL search), scoped to the active company — and every answer is logged.";
}

export function AiPanel() {
  const s = useLedger();
  const [q, setQ] = useState('');
  const [log, setLog] = useState<{ q: string; a: string }[]>([]);
  const ask = (text: string) => { if (!text.trim()) return; setLog([...log, { q: text, a: answer(s, text) }]); setQ(''); };
  return (
    <div className="ai-panel">
      <div className="ai-head"><Sparkles size={18} /> Ask your books <span className="pill">offline demo · Claude in Phase 4</span></div>
      <div className="ai-log">
        {!log.length && <div className="ai-empty">Ask a question about this company's books, or pick a suggestion.</div>}
        {log.map((m, i) => (
          <div key={i} className="ai-turn"><div className="ai-q">{m.q}</div><div className="ai-a">{m.a}</div></div>
        ))}
      </div>
      <div className="ai-sugg">{SUGGESTIONS.map((x) => <button key={x} className="chip" onClick={() => ask(x)}>{x}</button>)}</div>
      <form className="ai-input" onSubmit={(e) => { e.preventDefault(); ask(q); }}>
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="e.g. Who owes me the most?" />
        <button className="btn primary" type="submit"><Send size={14} /> Ask</button>
      </form>
    </div>
  );
}
