import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Save, Undo2, Redo2, ChevronDown, Lightbulb, Share2, X, Check, Plus, ChevronLeft, ChevronRight, Grid3x3, Columns3, PanelBottom, Minus,
  Copy, ArrowDownAZ, ArrowUpAZ, Funnel, Sigma, Download, Printer, FileText, HandCoins, FilePlus, Banknote, Receipt, NotebookPen,
  Car, Wallet, BadgeCheck, CalendarDays, ChevronUp,
} from 'lucide-react';
import './App.css';
import { store, useLedger } from './engine/store';
import { markPayrollPaid, PostingError, postPayroll, runDepreciation } from './engine/ledger';
import { SCREENS, TABS, screenById, type FormKind, type Row, type RowMeta, type Col } from './modules/registry';
import { Grid, colName, fmt, cellValue, type Sel } from './components/Grid';
import { Ribbon, type ActionGroup } from './components/Ribbon';
import { JE_COLUMNS, jeEditable, jeEdit, jeRows, JeHeader, postDraft, useJeDraft } from './components/JournalEntry';
import { DocForm } from './components/DocForm';
import { AiPanel } from './components/AiPanel';
import { Backstage } from './components/Backstage';

const USER = 'owner@aegisbooks.local';
const ORIGIN: Sel = { r: 1, c: 0, r2: 1, c2: 0 };
const C = { green: '#33b36b', blue: '#4f9bea', orange: '#f0a33a', red: '#e5534b', teal: '#2bb3a8', gray: '#b8b8b8', purple: '#a57be8' };
const monthEnd = (d = new Date()) => new Date(Date.UTC(d.getFullYear(), d.getMonth() + 1, 0)).toISOString().slice(0, 10);

export default function App() {
  const s = useLedger();
  const draft = useJeDraft();
  const [open, setOpen] = useState<string[]>(['dashboard', 'chart-of-accounts', 'sales-invoices', 'trial-balance']);
  const [active, setActive] = useState('dashboard');
  const [tab, setTab] = useState('Home');
  const [sels, setSels] = useState<Record<string, Sel>>({});
  const [sorts, setSorts] = useState<Record<string, { key: string; dir: 1 | -1 }>>({});
  const [filter, setFilter] = useState<{ on: boolean; text: string }>({ on: false, text: '' });
  const [zoom, setZoom] = useState(100);
  const [form, setForm] = useState<FormKind | null>(null);
  const [backstage, setBackstage] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const [status, setStatus] = useState<{ msg: string; kind: 'ok' | 'err' } | null>(null);
  const [q, setQ] = useState('');
  const [qOpen, setQOpen] = useState(false);
  const statusTimer = useRef<number | undefined>(undefined);

  const screen = screenById(active);
  const sel = sels[active] ?? ORIGIN;
  const setSel = (x: Sel) => setSels((p) => ({ ...p, [active]: x }));

  const notify = (msg: string, kind: 'ok' | 'err' = 'ok') => {
    setStatus({ msg, kind });
    window.clearTimeout(statusTimer.current);
    statusTimer.current = window.setTimeout(() => setStatus(null), kind === 'err' ? 9000 : 6000);
  };

  const openSheet = (id: string) => {
    setOpen((o) => (o.includes(id) ? o : [...o, id]));
    setActive(id);
    setFilter({ on: false, text: '' });
    const sc = screenById(id);
    if (sc.planned && sc.note) notify(sc.note);
  };
  const closeSheet = (id: string) => {
    const next = open.filter((x) => x !== id);
    if (!next.length) return;
    setOpen(next);
    if (active === id) setActive(next[Math.max(0, open.indexOf(id) - 1)]);
  };

  // ---------- view model for the active sheet
  const view = useMemo(() => {
    if (screen.kind === 'journal-entry') return { columns: JE_COLUMNS, rows: jeRows(s, draft), editable: jeEditable, onEdit: jeEdit };
    if (screen.kind === 'ai') return { columns: [] as Col[], rows: [] as Row[] };
    let rows = screen.rows?.(s) ?? [];
    const columns = screen.columns ?? [];
    if (filter.on && filter.text.trim()) {
      const f = filter.text.toLowerCase();
      rows = rows.filter((r) => !(r._meta as RowMeta | undefined)?.style?.match(/total|grand|group/) && columns.some((c) => String(r[c.key] ?? '').toLowerCase().includes(f)));
    }
    const sort = sorts[active];
    if (sort) {
      const body = rows.filter((r) => !(r._meta as RowMeta | undefined)?.style?.match(/total|grand/));
      const tail = rows.filter((r) => (r._meta as RowMeta | undefined)?.style?.match(/total|grand/));
      body.sort((a, b) => {
        const x = a[sort.key], y = b[sort.key];
        if (typeof x === 'number' && typeof y === 'number') return (x - y) * sort.dir;
        return String(x ?? '').localeCompare(String(y ?? ''), undefined, { numeric: true }) * sort.dir;
      });
      rows = [...body, ...tail];
    }
    return { columns, rows };
  }, [screen, s, draft, filter, sorts, active]);

  // ---------- selection stats (Excel status bar)
  const stats = useMemo(() => {
    const vals: number[] = [];
    let count = 0;
    for (let r = Math.min(sel.r, sel.r2); r <= Math.max(sel.r, sel.r2); r++) {
      if (r === 0) continue;
      for (let c = Math.min(sel.c, sel.c2); c <= Math.max(sel.c, sel.c2); c++) {
        const v = cellValue(view.rows[r - 1], view.columns[c]);
        if (v !== undefined && v !== '') count++;
        if (typeof v === 'number') vals.push(v);
      }
    }
    const sum = vals.reduce((a, b) => a + b, 0);
    return { count, n: vals.length, sum, avg: vals.length ? sum / vals.length : 0 };
  }, [sel, view]);

  // ---------- formula bar content
  const activeCol = view.columns[sel.c];
  const activeRow = sel.r === 0 ? undefined : view.rows[sel.r - 1];
  const activeMeta = activeRow?._meta as RowMeta | undefined;
  const rawActive = sel.r === 0 ? activeCol?.label : cellValue(activeRow, activeCol);
  const formula = activeMeta?.formula && activeCol?.type === 'money' && typeof rawActive === 'number'
    ? `=SUM(${colName(sel.c)}2:${colName(sel.c)}${sel.r})`
    : rawActive === undefined ? '' : typeof rawActive === 'number' ? String(rawActive) : String(rawActive);
  const editableActive = !!view.editable && sel.r >= 1 && view.editable(sel.r - 1, sel.c);

  // ---------- commands
  const toCsv = () => {
    const esc = (v: unknown) => { const x = v === undefined ? '' : String(v); return /[",\n]/.test(x) ? `"${x.replace(/"/g, '""')}"` : x; };
    const lines = [view.columns.map((c) => esc(c.label)).join(','), ...view.rows.map((r) => view.columns.map((c) => esc(r[c.key])).join(','))];
    const blob = new Blob([lines.join('\r\n')], { type: 'text/csv' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `${screen.label.replace(/[^\w]+/g, '-')}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
    notify(`Exported ${view.rows.length} rows to ${a.download}`);
  };
  const copy = () => {
    const out: string[] = [];
    for (let r = Math.min(sel.r, sel.r2); r <= Math.max(sel.r, sel.r2); r++) {
      const cells: string[] = [];
      for (let c = Math.min(sel.c, sel.c2); c <= Math.max(sel.c, sel.c2); c++) cells.push(r === 0 ? view.columns[c]?.label ?? '' : fmt(view.columns[c], cellValue(view.rows[r - 1], view.columns[c])));
      out.push(cells.join('\t'));
    }
    navigator.clipboard?.writeText(out.join('\n')).then(() => notify('Copied selection'), () => notify('Clipboard blocked by browser', 'err'));
  };
  const sortBy = (dir: 1 | -1) => {
    const col = view.columns[sel.c];
    if (!col || screen.kind) return notify('Select a column in a list sheet to sort', 'err');
    setSorts((p) => ({ ...p, [active]: { key: col.key, dir } }));
    notify(`Sorted by ${col.label} ${dir === 1 ? 'A→Z' : 'Z→A'}`);
  };
  const run = (label: string, fn: () => string) => {
    try { notify(store.mutate(() => fn()) || label); } catch (e) { notify(e instanceof PostingError ? e.message : String(e), 'err'); }
  };
  const postJv = () => {
    if (active !== 'journal-voucher') { openSheet('journal-voucher'); return; }
    const r = postDraft(USER);
    notify(r.msg, r.ok ? 'ok' : 'err');
  };

  const newDoc = (k: FormKind) => ({ run: () => setForm(k) });
  const extra: Record<string, ActionGroup[]> = {
    Home: [
      { group: 'Clipboard', position: 'start', actions: [
        { label: 'Copy', icon: Copy, color: C.gray, small: true, run: copy, title: 'Copy (Ctrl+C)' },
        { label: 'Export CSV', icon: Download, color: C.gray, small: true, run: toCsv },
        { label: 'Print', icon: Printer, color: C.gray, small: true, run: () => window.print() },
      ] },
      { group: 'Quick Entry', position: 'start', actions: [
        { label: 'New Invoice', icon: FileText, color: C.green, ...newDoc('sales-invoice') },
        { label: 'New Receipt', icon: HandCoins, color: C.green, ...newDoc('receipt') },
        { label: 'New Bill', icon: FilePlus, color: C.orange, ...newDoc('purchase-invoice') },
        { label: 'New Payment', icon: Banknote, color: C.orange, ...newDoc('payment') },
        { label: 'New Expense', icon: Receipt, color: C.red, ...newDoc('expense') },
        { label: 'Journal', icon: NotebookPen, color: C.blue, run: () => openSheet('journal-voucher') },
      ] },
      { group: 'Editing', actions: [
        { label: 'Sort A → Z', icon: ArrowDownAZ, color: C.blue, small: true, run: () => sortBy(1) },
        { label: 'Sort Z → A', icon: ArrowUpAZ, color: C.blue, small: true, run: () => sortBy(-1) },
        { label: 'Clear Sort', icon: X, color: C.gray, small: true, run: () => setSorts((p) => { const n = { ...p }; delete n[active]; return n; }) },
        { label: filter.on ? 'Filter: On' : 'Filter', icon: Funnel, color: C.teal, small: true, run: () => setFilter((f) => ({ on: !f.on, text: '' })) },
        { label: 'AutoSum', icon: Sigma, color: C.gray, small: true, run: () => notify(stats.n ? `Sum of ${stats.n} cells = ${fmt({ key: '', label: '', type: 'money' }, stats.sum)}` : 'Select numeric cells first', stats.n ? 'ok' : 'err') },
      ] },
    ],
    Finance: [{ group: 'Post', actions: [{ label: 'Post Voucher', icon: BadgeCheck, color: C.green, run: postJv, title: 'Post the journal voucher (Ctrl+Enter)' }] }],
    Receivables: [{ group: 'New', actions: [{ label: 'New Invoice', icon: FileText, color: C.green, ...newDoc('sales-invoice') }, { label: 'New Receipt', icon: HandCoins, color: C.green, ...newDoc('receipt') }] }],
    Payables: [{ group: 'New', actions: [{ label: 'New Bill', icon: FilePlus, color: C.orange, ...newDoc('purchase-invoice') }, { label: 'New Payment', icon: Banknote, color: C.orange, ...newDoc('payment') }, { label: 'New Expense', icon: Receipt, color: C.red, ...newDoc('expense') }] }],
    'CRM & Assets': [{ group: 'Run', actions: [{ label: 'Run Depreciation', icon: Car, color: C.teal, run: () => run('', () => `Posted ${runDepreciation(store.get(), monthEnd(), USER)} for ${monthEnd().slice(0, 7)}`) }] }],
    'HR & Payroll': [{ group: 'Run', actions: [
      { label: 'Run Payroll', icon: Wallet, color: C.green, run: () => run('', () => {
        const period = new Date().toLocaleString('en', { month: 'short', year: 'numeric' });
        if (store.get().payrollRuns.some((r) => r.period === period)) throw new PostingError(`Payroll for ${period} already posted.`);
        return `Posted payroll ${postPayroll(store.get(), period, monthEnd(), USER, false)} (${period}) — accrued, not yet paid`;
      }) },
      { label: 'Mark Paid (WPS)', icon: BadgeCheck, color: C.blue, run: () => run('', () => {
        const unpaid = store.get().payrollRuns.filter((r) => !r.isPaid);
        if (!unpaid.length) throw new PostingError('No unpaid payroll runs.');
        unpaid.forEach((r) => markPayrollPaid(store.get(), r.no, r.runDate, USER));
        return `Paid ${unpaid.length} payroll run(s) via WPS transfer`;
      }) },
    ] }],
    Reports: [{ group: 'Output', actions: [{ label: 'Export CSV', icon: Download, color: C.gray, run: toCsv }, { label: 'Print', icon: Printer, color: C.gray, run: () => window.print() }] }],
    Settings: [{ group: 'Period End', actions: [{ label: 'Close Period', icon: CalendarDays, color: C.red, run: () => run('', () => {
      const p = store.get().periods.find((x) => !x.isClosed);
      if (!p || p.endDate >= new Date().toISOString().slice(0, 10)) throw new PostingError('No completed period left to close.');
      p.isClosed = true;
      return `Closed ${p.name} — postings dated in it are now rejected`;
    }) }] }],
  };

  // ---------- global keys
  useEffect(() => {
    const k = (e: KeyboardEvent) => {
      if (e.ctrlKey && e.key === 'Enter' && active === 'journal-voucher') { e.preventDefault(); postJv(); }
      if (e.altKey && (e.key === 'q' || e.key === 'Q')) { e.preventDefault(); document.getElementById('tellme')?.focus(); }
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  });

  const results = q.trim()
    ? SCREENS.filter((x) => `${x.label} ${x.tab} ${x.group}`.toLowerCase().includes(q.toLowerCase())).slice(0, 8)
    : [];

  const statusMsg = status?.msg ?? (editableActive ? 'Enter' : screen.note && active === 'journal-voucher' ? screen.note : 'Ready');

  return (
    <div className="app">
      <header className="titlebar">
        <div className="tb-left"><div className="logo">A</div></div>
        <div className="tb-title">{s.company.name} - Aegis Books</div>
        <div className="tb-right"><span className="tb-user">Firm Admin</span><span className="avatar">FA</span></div>
      </header>

      <nav className="menubar">
        <button className="mb-file" onClick={() => setBackstage(true)}>File</button>
        {TABS.map((t) => (
          <button key={t} className={t === tab ? 'mb-tab on' : 'mb-tab'} onClick={() => { setTab(t); setCollapsed(false); }}>{t}</button>
        ))}
        <div className="tellme">
          <Lightbulb size={16} />
          <input
            id="tellme"
            value={q}
            onChange={(e) => { setQ(e.target.value); setQOpen(true); }}
            onFocus={() => setQOpen(true)}
            onBlur={() => setTimeout(() => setQOpen(false), 150)}
            onKeyDown={(e) => { if (e.key === 'Enter' && results[0]) { openSheet(results[0].id); setTab(results[0].tab); setQ(''); (e.target as HTMLInputElement).blur(); } if (e.key === 'Escape') (e.target as HTMLInputElement).blur(); }}
            placeholder="Tell me what you want to do"
          />
          {qOpen && results.length > 0 && (
            <div className="tellme-results">
              {results.map((x) => { const I = x.icon; return (
                <button key={x.id} onMouseDown={() => { openSheet(x.id); setTab(x.tab); setQ(''); }}>
                  <I size={16} color={x.color} /> <span>{x.label}</span> <em>{x.tab} › {x.group}</em>
                </button>
              ); })}
            </div>
          )}
        </div>
        <button className="share" onClick={toCsv}><Share2 size={14} /> Export <ChevronDown size={12} /></button>
      </nav>

      {!collapsed && <Ribbon tab={tab} active={active} onOpen={openSheet} extra={extra[tab] ?? []} onCollapse={() => setCollapsed(true)} />}

      <div className="qat">
        <button title="Save (auto-saved)" onClick={() => notify('All changes are posted immediately — nothing to save')}><Save size={16} /></button>
        <button title="Undo — posted vouchers are reversed, not undone" disabled><Undo2 size={16} /></button>
        <button title="Redo" disabled><Redo2 size={16} /></button>
        {collapsed && <button title="Show the Ribbon" onClick={() => setCollapsed(false)}><ChevronUp size={16} style={{ transform: 'rotate(180deg)' }} /></button>}
        <span className="qat-sep" />
        <span className="qat-crumb">{screen.tab} › {screen.group} › <b>{screen.label}</b></span>
      </div>

      <div className="formulabar">
        <div className="namebox">{screen.kind === 'ai' ? '' : `${colName(sel.c)}${sel.r + 1}`}<ChevronDown size={12} /></div>
        <div className="fb-sep">⋮</div>
        <button className="fb-btn" disabled><X size={16} /></button>
        <button className="fb-btn" disabled><Check size={16} /></button>
        <div className="fb-fx"><i>fx</i></div>
        {editableActive ? (
          <input className="fb-input" key={`${sel.r}-${sel.c}`} defaultValue={formula} onKeyDown={(e) => { if (e.key === 'Enter') { view.onEdit?.(sel.r - 1, sel.c, (e.target as HTMLInputElement).value); setSel({ r: sel.r + 1, c: sel.c, r2: sel.r + 1, c2: sel.c }); } }} />
        ) : (
          <div className="fb-input ro">{formula}</div>
        )}
        {filter.on && (
          <input className="fb-filter" autoFocus placeholder="Filter rows…" value={filter.text} onChange={(e) => setFilter({ on: true, text: e.target.value })} />
        )}
      </div>

      <main className="sheet">
        {screen.kind === 'journal-entry' && <JeHeader onPost={postJv} />}
        {screen.kind === 'ai' ? (
          <AiPanel />
        ) : (
          <Grid columns={view.columns} rows={view.rows} sel={sel} onSel={setSel} zoom={zoom} editable={view.editable} onEdit={view.onEdit} />
        )}
      </main>

      <footer className="sheettabs">
        <button className="st-nav" onClick={() => { const i = open.indexOf(active); if (i > 0) setActive(open[i - 1]); }}><ChevronLeft size={14} /></button>
        <button className="st-nav" onClick={() => { const i = open.indexOf(active); if (i < open.length - 1) setActive(open[i + 1]); }}><ChevronRight size={14} /></button>
        <div className="st-tabs">
          {open.map((id) => (
            <div key={id} className={id === active ? 'st-tab on' : 'st-tab'} onMouseDown={(e) => { if (e.button === 1) { e.preventDefault(); closeSheet(id); } else { setActive(id); } }} title="Middle-click to close">
              {screenById(id).label}
              {open.length > 1 && <span className="st-x" onMouseDown={(e) => { e.stopPropagation(); closeSheet(id); }}>×</span>}
            </div>
          ))}
          <button className="st-add" title="Find a sheet (Alt+Q)" onClick={() => document.getElementById('tellme')?.focus()}><Plus size={16} /></button>
        </div>
      </footer>

      <div className="statusbar">
        <span className={status?.kind === 'err' ? 'sb-msg err' : status ? 'sb-msg ok' : 'sb-msg'}>{statusMsg}</span>
        <span className="sb-grow" />
        {stats.n > 1 && <span className="sb-stat">Average: {fmt({ key: '', label: '', type: 'money' }, stats.avg)}</span>}
        {stats.count > 1 && <span className="sb-stat">Count: {stats.count}</span>}
        {stats.n > 1 && <span className="sb-stat">Sum: {fmt({ key: '', label: '', type: 'money' }, stats.sum)}</span>}
        <span className="sb-views"><Grid3x3 size={14} className="on" /><Columns3 size={14} /><PanelBottom size={14} /></span>
        <button className="sb-zoom" onClick={() => setZoom((z) => Math.max(60, z - 10))} aria-label="Zoom out"><Minus size={12} /></button>
        <input type="range" min={60} max={160} step={10} value={zoom} onChange={(e) => setZoom(Number(e.target.value))} aria-label="Zoom" />
        <button className="sb-zoom" onClick={() => setZoom((z) => Math.min(160, z + 10))} aria-label="Zoom in"><Plus size={12} /></button>
        <span className="sb-pct">{zoom}%</span>
      </div>

      {form && <DocForm kind={form} user={USER} onClose={() => setForm(null)} onDone={(msg) => {
        const target: Record<FormKind, string> = { 'sales-invoice': 'sales-invoices', 'purchase-invoice': 'purchase-invoices', receipt: 'receipts', payment: 'payments', expense: 'expenses' };
        setForm(null); openSheet(target[form]); notify(msg);
      }} />}
      {backstage && <Backstage onClose={() => setBackstage(false)} onExport={toCsv} />}
    </div>
  );
}
