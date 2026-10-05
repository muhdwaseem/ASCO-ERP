// Line editor for Invoice / Quotation / Bill Entry: search bar, one row per line (item, account, quantity
// stepper, rate, discount, VAT switch, cost centre), a Notes box and a live totals panel.
// It edits the SAME draft as the Grid view (EntrySheets), so Ctrl+Enter, Save draft, Clear and pasting
// from Excel behave identically in both; only the way the lines are drawn differs.
import { useEffect, useRef, useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { Minus, Plus, Search, Trash2 } from 'lucide-react';
import type { Lookups } from '../api/client';
import { money } from '../modules/sheetKit';
import {
  docAddLines, docCfg, docEditCell, docItem, docLineCalc, docLineKeys, docLineProblem, docLineUsed, docOptions, docRemoveLine, docSet, docState, docSummary,
  growEntrySheet, useEntryStores, type DocKind,
} from './EntrySheets';

// ---------------------------------------------------------------- search-as-you-type input
// Commits on pick / Enter / Tab / leaving the field (not on every keystroke), so a half-typed word never
// gets replaced by a different match mid-typing.
function PickInput({ value, options, placeholder, onCommit, clearOnCommit, className, attrs }: {
  value: string; options: string[]; placeholder?: string; onCommit: (v: string) => void; clearOnCommit?: boolean; className?: string; attrs?: Record<string, string | number>;
}) {
  const [text, setText] = useState(value);
  const [open, setOpen] = useState(false);
  const [hi, setHi] = useState(0);
  const skipBlur = useRef(false);
  useEffect(() => { setText(value); }, [value]); // eslint-disable-line react-hooks/set-state-in-effect
  const q = text.trim().toLowerCase();
  const matches = options.filter((o) => !q || o.toLowerCase().includes(q)).slice(0, 8);
  const show = open && matches.length > 0 && !options.includes(text);
  const commit = (v: string) => { skipBlur.current = true; setOpen(false); onCommit(v); setText(clearOnCommit ? '' : v); };
  const best = () => (show ? matches[Math.min(hi, matches.length - 1)] : text);
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setHi((h) => Math.min(matches.length - 1, h + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setHi((h) => Math.max(0, h - 1)); }
    else if (e.key === 'Enter' && !e.ctrlKey) { e.preventDefault(); commit(best()); }
    else if (e.key === 'Tab' && text.trim() && show) { commit(best()); }
    else if (e.key === 'Escape') { setText(value); setOpen(false); }
  };
  return (
    <span className="le-pick">
      <input className={`le-input ${className ?? ''}`} value={text} placeholder={placeholder} {...attrs}
        onChange={(e) => { setText(e.target.value); setOpen(true); setHi(0); }}
        onFocus={() => setOpen(true)} onClick={() => setOpen(true)}
        onBlur={() => { setOpen(false); if (skipBlur.current) { skipBlur.current = false; return; } if (text !== value) { onCommit(text); if (clearOnCommit) setText(''); } }}
        onKeyDown={onKey} />
      {show && (
        <div className="cell-suggest le-suggest" onMouseDown={(e) => e.preventDefault()}>
          {matches.map((o, i) => <div key={o} className={i === hi ? 'on' : ''} onMouseDown={() => commit(o)}>{o}</div>)}
        </div>
      )}
    </span>
  );
}

const aed = (x: number) => `AED ${money(x)}`;
const MIN_ROWS = 3;

export function LineEditor({ id: k, L, stock, onNotice }: {
  id: DocKind; L: Lookups | undefined; stock?: Record<string, number>; onNotice?: (msg: string, kind?: 'ok' | 'err') => void;
}) {
  useEntryStores(); // re-render whenever the draft changes (from here or from the Grid view)
  const [extra, setExtra] = useState(MIN_ROWS);
  const wrap = useRef<HTMLDivElement>(null);
  const search = useRef<HTMLDivElement>(null);
  if (!L) return <div className="le-wrap"><div className="le-card le-empty">Loading lists…</div></div>;

  const cfg = docCfg(k);
  const d = docState(k);
  const keys = docLineKeys(k);
  const col = (key: string) => keys.indexOf(key as never);
  const opts = docOptions(k, L);
  const sum = docSummary(k, L);
  const hasDisc = k === 'invoice-entry';

  const lastUsed = d.lines.reduce((m, l, i) => (docLineUsed(l) ? i : m), -1);
  const visible = Math.min(d.lines.length, Math.max(extra, lastUsed + 2));
  const usedCount = d.lines.filter(docLineUsed).length;

  const edit = (r: number, key: string, v: string) => docEditCell(k, L, r, col(key), v);
  const addLineBySearch = (label: string) => {
    if (!label.trim()) return;
    const it = docItem(L, label);
    if (!it) { onNotice?.(`No item matches "${label}". Pick one from the list, or type the description in a line below.`, 'err'); return; }
    let r = d.lines.findIndex((l) => !docLineUsed(l));
    if (r < 0) { docAddLines(k, 3); r = d.lines.length; }
    edit(r, 'item', `${it.code} · ${it.name}`);
  };
  const stockBadge = (code: string, kind: string) => {
    if (kind === 'Service') return <span className="le-badge svc">Service</span>;
    const q = stock?.[code];
    if (q === undefined) return <span className="le-badge neutral">Goods</span>;
    return <span className={`le-badge ${q > 5 ? 'ok' : 'low'}`}>{q > 5 ? `In stock ${q}` : `Low stock ${q}`}</span>;
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'F2') { e.preventDefault(); search.current?.querySelector('input')?.focus(); }
  };
  // Paste a block of cells (Excel / Google Sheets / the Grid view): columns land from the focused field rightwards, in grid order.
  const onPaste = (e: ClipboardEvent<HTMLDivElement>) => {
    const text = e.clipboardData.getData('text/plain');
    if (!/[\t\n]/.test(text.replace(/\r?\n$/, ''))) return; // a single value: normal paste into the field
    const at = (e.target as HTMLElement).closest('[data-col]') as HTMLElement | null;
    const rows = text.replace(/\r\n?/g, '\n').replace(/\n$/, '').split('\n').map((l) => l.split('\t'));
    const r0 = at ? Number(at.dataset.row) : Math.max(0, d.lines.findIndex((l) => !docLineUsed(l)));
    const c0 = at ? Number(at.dataset.col) : 0;
    e.preventDefault();
    growEntrySheet(k, r0 + rows.length - 2);
    let n = 0;
    rows.forEach((cells, i) => cells.forEach((v, j) => { if (c0 + j < keys.length && v.trim() !== '') { docEditCell(k, L, r0 + i, c0 + j, v.trim()); n++; } }));
    setExtra((x) => Math.max(x, r0 + rows.length + 1));
    onNotice?.(n ? `Pasted ${n} value${n === 1 ? '' : 's'} into ${rows.length} line${rows.length === 1 ? '' : 's'}` : 'Nothing pasted', n ? 'ok' : 'err');
  };

  return (
    <div className="le-wrap" ref={wrap} onKeyDown={onKeyDown} onPaste={onPaste}>
      <section className="le-card">
        <div className="le-top">
          <div className="le-search" ref={search}>
            <Search size={16} className="le-search-ico" />
            <PickInput value="" options={opts.items} placeholder="Scan barcode or type item code / name" onCommit={addLineBySearch} clearOnCommit />
            <kbd>F2</kbd>
          </div>
          <span className="le-count">{usedCount} {usedCount === 1 ? 'line' : 'lines'}</span>
        </div>

        <table className="le-table">
          <thead>
            <tr>
              <th className="le-n">#</th><th>Item</th><th>Description</th><th>{cfg.party === 'Customer' ? 'Revenue account' : 'Expense account'}</th>
              <th>Qty</th><th>Unit</th><th className="r">Rate</th>{hasDisc && <th className="r">Disc %</th>}<th>VAT</th><th>Cost centre</th><th className="r">Total</th><th />
            </tr>
          </thead>
          <tbody>
            {d.lines.slice(0, visible).map((l, i) => {
              const used = docLineUsed(l);
              const c = docLineCalc(k, l);
              const problem = used ? docLineProblem(k, L, l) : null;
              const it = l.item ? docItem(L, l.item) : undefined;
              const qty = Number(l.qty.replace(/,/g, '')) || 0;
              const attrs = (key: string) => ({ 'data-row': i, 'data-col': col(key) });
              return (
                <tr key={i} className={`${used ? '' : 'blank'}${problem ? ' warn' : ''}`} title={problem ?? undefined}>
                  <td className="le-n">{i + 1}</td>
                  <td className="le-item">
                    <PickInput value={l.item} options={opts.items} placeholder="Item (optional)" onCommit={(v) => edit(i, 'item', v)} attrs={attrs('item')} />
                    {it && <span className="le-sub">{it.code} {stockBadge(it.code, it.kind)}</span>}
                  </td>
                  <td><input className="le-input" value={l.description} placeholder="Description" {...attrs('description')} onChange={(e) => edit(i, 'description', e.target.value)} /></td>
                  <td><PickInput value={l.account} options={opts.accounts} placeholder="Account" onCommit={(v) => edit(i, 'account', v)} attrs={attrs('account')} /></td>
                  <td>
                    <span className="le-step">
                      <button type="button" tabIndex={-1} aria-label="Less" onClick={() => edit(i, 'qty', String(Math.max(0, qty - 1)))}><Minus size={13} /></button>
                      <input className="le-input le-qty" inputMode="decimal" value={l.qty} {...attrs('qty')} onChange={(e) => edit(i, 'qty', e.target.value)} />
                      <button type="button" tabIndex={-1} aria-label="More" onClick={() => edit(i, 'qty', String(qty + 1))}><Plus size={13} /></button>
                    </span>
                  </td>
                  <td className="le-unit">{it?.unit ?? ''}</td>
                  <td><input className="le-input r le-rate" inputMode="decimal" value={l.rate} placeholder="0.00" {...attrs('rate')} onChange={(e) => edit(i, 'rate', e.target.value)} /></td>
                  {hasDisc && <td><input className="le-input r le-disc" inputMode="decimal" value={l.disc} placeholder="0" {...attrs('disc')} onChange={(e) => edit(i, 'disc', e.target.value)} /></td>}
                  <td>
                    <span className="le-vat" role="group" aria-label="VAT">
                      <button type="button" tabIndex={-1} className={(l.vat || '5%') === '5%' ? 'on' : ''} onClick={() => edit(i, 'vat', '5%')}>5%</button>
                      <button type="button" tabIndex={-1} className={l.vat === '0%' ? 'on' : ''} onClick={() => edit(i, 'vat', '0%')}>0%</button>
                    </span>
                  </td>
                  <td><PickInput value={l.cc} options={opts.costCentres} placeholder={d.cc ? `${d.cc.split(' ')[0]} (header)` : 'Optional'} onCommit={(v) => edit(i, 'cc', v)} attrs={attrs('cc')} /></td>
                  <td className={`r le-total${used ? '' : ' dim'}`}>{used ? money(c.total) : ''}</td>
                  <td><button type="button" className="le-del" tabIndex={-1} aria-label={`Delete line ${i + 1}`} onClick={() => docRemoveLine(k, i)}><Trash2 size={14} /></button></td>
                </tr>
              );
            })}
          </tbody>
        </table>

        <div className="le-add">
          <button type="button" className="btn ghost" onClick={() => { if (visible >= d.lines.length) docAddLines(k, 3); setExtra(visible + 1); }}><Plus size={14} /> Add line</button>
          <span className="le-hint">Tab moves between fields · F2 searches items · paste a block from Excel into any field</span>
        </div>

        <div className="le-foot">
          <label className="le-notes">Notes on the {cfg.noun}
            <textarea value={d.narration} placeholder="Optional. Printed text or internal note" onChange={(e) => docSet(k, { narration: e.target.value })} />
          </label>
          <div className="le-sum" aria-live="polite">
            <div><span>Subtotal</span><span>{money(sum.gross)}</span></div>
            {hasDisc && <div className="neg"><span>Discount</span><span>{sum.discount ? `−${money(sum.discount)}` : '0.00'}</span></div>}
            <div><span>VAT</span><span>{money(sum.vat)}</span></div>
            <div className="grand"><span>Grand total</span><span>{aed(sum.total)}</span></div>
            <div className={`le-status${sum.warn ? ' warn' : sum.ready ? ' ok' : ''}`}><i />{sum.status}</div>
          </div>
        </div>
      </section>
    </div>
  );
}
