// Ask AI. Live mode: Claude with read-only tools over the active company's books (server/Asco.Api/Ai).
// Demo mode: a few deterministic answers computed from the in-browser ledger.
import { useState } from 'react';
import { Sparkles, Send } from 'lucide-react';
import { useLedger } from '../engine/store';
import { aging, balances, naturalBalance, pnl, ACC, r2, partyName, invoiceOutstanding } from '../engine/ledger';
import { FY_START, TODAY } from '../modules/registry';
import type { LedgerState } from '../engine/types';
import { api } from '../api/client';

const SUGGESTIONS = ['What is my net profit this year?', 'Who owes me the most?', 'How much cash do I have?', 'What is my VAT payable?', 'Which invoices are overdue?'];
const f = (n: number) => `AED ${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

function demoAnswer(s: LedgerState, q: string): string {
  const x = q.toLowerCase();
  const b = balances(s);
  const bal = (c: string) => (b.has(c) ? naturalBalance(s, c, b.get(c)!) : 0);
  if (/profit|loss|margin|earn/.test(x)) {
    const p = pnl(s, FY_START, TODAY);
    return `Year to date revenue is ${f(p.totalRev)} against expenses of ${f(p.totalExp)}, so net profit is ${f(p.net)} (${p.totalRev ? ((p.net / p.totalRev) * 100).toFixed(1) : 0}% margin).`;
  }
  if (/owe|debtor|receivable|customer/.test(x)) {
    const top = aging(s, 'AR', TODAY).sort((a, c) => c.total - a.total);
    return top.length ? `${top[0].name} owes the most: ${f(top[0].total)}. Total receivables: ${f(r2(top.reduce((a, r) => a + r.total, 0)))} across ${top.length} customers.` : 'No customer owes you anything right now.';
  }
  if (/cash|bank|liquid/.test(x)) return `Cash & bank stands at ${f(r2(bal(ACC.BANK) + bal(ACC.PETTY)))}.`;
  if (/vat|tax/.test(x)) return `Output VAT ${f(bal(ACC.VAT_OUT))} − input VAT ${f(bal(ACC.VAT_IN))} = net VAT payable ${f(r2(bal(ACC.VAT_OUT) - bal(ACC.VAT_IN)))}.`;
  if (/overdue|late|chase/.test(x)) {
    const od = s.salesInvoices.filter((i) => i.dueDate < TODAY && invoiceOutstanding(s, i.no) > 0);
    return od.length ? `${od.length} overdue: ${od.map((i) => `${i.no} (${partyName(s, i.party)}, ${f(invoiceOutstanding(s, i.no))})`).join('; ')}.` : 'Nothing is overdue.';
  }
  return 'The offline demo only knows a few questions. Sign in to a live company to ask Claude anything about your books.';
}

interface Turn { q: string; a: string; meta?: string; err?: boolean }

export function AiPanel({ companyId }: { companyId?: number }) {
  const s = useLedger();
  const live = companyId !== undefined;
  const [q, setQ] = useState('');
  const [log, setLog] = useState<Turn[]>([]);
  const [busy, setBusy] = useState(false);

  const ask = async (text: string) => {
    if (!text.trim() || busy) return;
    setQ('');
    if (!live) { setLog([...log, { q: text, a: demoAnswer(s, text) }]); return; }
    setBusy(true);
    const history = log.filter((t) => !t.err).flatMap((t) => [{ role: 'user', text: t.q }, { role: 'assistant', text: t.a }]).slice(-12);
    try {
      const r = await api.post<{ answer: string; toolsUsed: string[]; model: string; inputTokens: number; outputTokens: number }>('/ai/ask', { question: text, history }, companyId!);
      const tools = r.toolsUsed.length ? `checked: ${r.toolsUsed.map((t) => t.replace(/^get_|_/g, ' ').trim()).join(', ')}` : 'no reports needed';
      setLog((l) => [...l, { q: text, a: r.answer, meta: `${tools} · ${r.model} · ${r.inputTokens + r.outputTokens} tokens` }]);
    } catch (e) {
      setLog((l) => [...l, { q: text, a: e instanceof Error ? e.message : String(e), err: true }]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="ai-panel">
      <div className="ai-head"><Sparkles size={18} /> Ask your books <span className="pill">{live ? 'Claude · read-only access to this company' : 'offline demo'}</span></div>
      <div className="ai-log">
        {!log.length && <div className="ai-empty">{live ? 'Ask anything about this company’s books — Claude looks up the trial balance, P&L, aging, ledger and more, and cites where each figure came from. It can’t post or change anything.' : 'Ask a question about the demo books, or pick a suggestion.'}</div>}
        {log.map((m, i) => (
          <div key={i} className="ai-turn">
            <div className="ai-q">{m.q}</div>
            <div className={m.err ? 'ai-a err' : 'ai-a'}>{m.a}</div>
            {m.meta && <div className="ai-meta">{m.meta}</div>}
          </div>
        ))}
        {busy && <div className="ai-a">Looking it up…</div>}
      </div>
      <div className="ai-sugg">{SUGGESTIONS.map((x) => <button key={x} className="chip" onClick={() => ask(x)} disabled={busy}>{x}</button>)}</div>
      <form className="ai-input" onSubmit={(e) => { e.preventDefault(); ask(q); }}>
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="e.g. Which customers are over their credit limit?" disabled={busy} />
        <button className="btn primary" type="submit" disabled={busy}><Send size={14} /> Ask</button>
      </form>
    </div>
  );
}
