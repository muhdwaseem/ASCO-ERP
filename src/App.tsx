import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Save, Undo2, Redo2, ChevronDown, Lightbulb, Share2, X, Check, Plus, ChevronLeft, ChevronRight, Grid3x3, Columns3, PanelBottom, Minus, Copy, ArrowDownAZ, ArrowUpAZ, Funnel, Sigma, Download, Printer, FileText, HandCoins, FilePlus, Banknote, Receipt, NotebookPen, Car, Wallet, BadgeCheck, CalendarDays, ChevronUp, RefreshCw, LogOut, UserPlus, Building2, Mail, FileDown, FileMinus, FileX, ClipboardList, Send, ArrowRightLeft, Warehouse, PackagePlus, PackageMinus, ArrowLeftRight, SlidersHorizontal, FileOutput, Layers, Factory, CircleCheck, CircleX, Briefcase, Truck, Route, Settings2, Database,
} from 'lucide-react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api, sessionActions, useSession, type Lookups } from './api/client';
import { LIVE } from './modules/live';
import { store, useLedger } from './engine/store';
import { markPayrollPaid, PostingError, postPayroll, runDepreciation } from './engine/ledger';
import { SCREENS, TABS, MODULE_TABS, screenById, type FormKind, type Row, type RowMeta, type Col, type Screen } from './modules/registry';
import { ModuleForm, MODULE_FORMS } from './components/ModuleForm';
import { Grid, colName, fmt, cellValue, type Sel } from './components/Grid';
import { Ribbon, type ActionGroup } from './components/Ribbon';
import { JE_COLUMNS, jeEditable, jeEdit, jeRows, JeHeader, postDraft, postDraftLive, demoResolver, useJeDraft, type AccountResolver } from './components/JournalEntry';
import { LiveDocForm, LIVE_TARGET, type LiveFormKind, type LivePrefill } from './components/LiveDocForm';
import { PrintDoc, type PrintKind } from './components/PrintDoc';
import { LineEditor } from './components/LineEditor';
import { EntryHeader, entryView, growEntrySheet, isDocSheet, isEntrySheet, useEntryMode, openDocsPath, postEntrySheet, toOpenDocs, useEntryStores } from './components/EntrySheets';
import { isToolSheet, ToolHeader, toolPath, toolView, useAssets, useToolStores } from './components/AccountingSheets';
import { REPORT_MENU } from './modules/reportMenu';
import { dateLabel, datedPath, defaultDates, filterByDate, type DateState } from './modules/reportDates';
import { ReportDates } from './components/ReportDates';
import type { MenuItem } from './components/Ribbon';
import { DocForm } from './components/DocForm';
import { AiPanel } from './components/AiPanel';
import { ScanBill } from './components/ScanBill';
import { Backstage } from './components/Backstage';

const USER = 'owner@asco.local';
const ORIGIN: Sel = { r: 1, c: 0, r2: 1, c2: 0 };
const C = { green: '#33b36b', blue: '#4f9bea', orange: '#f0a33a', red: '#e5534b', teal: '#2bb3a8', gray: '#b8b8b8', purple: '#a57be8' };
/** What the grid shows for the active sheet; entry sheets add editing + cell search. */
interface SheetView {
  columns: Col[];
  rows: Row[];
  editable?: (r: number, c: number) => boolean;
  onEdit?: (r: number, c: number, value: string) => void;
  optionsFor?: (r: number, c: number) => string[] | undefined;
  ready?: number;
}
const monthEnd = (d = new Date()) => new Date(Date.UTC(d.getFullYear(), d.getMonth() + 1, 0)).toISOString().slice(0, 10);

export default function App() {
  const s = useLedger();
  const draft = useJeDraft();
  const entryVersion = useEntryStores();
  const [open, setOpen] = useState<string[]>(['dashboard', 'chart-of-accounts', 'sales-invoices', 'trial-balance']);
  const [active, setActive] = useState('dashboard');
  const [tab, setTab] = useState('Home');
  const [sels, setSels] = useState<Record<string, Sel>>({});
  const [sorts, setSorts] = useState<Record<string, { key: string; dir: 1 | -1 }>>({});
  const [filter, setFilter] = useState<{ on: boolean; text: string }>({ on: false, text: '' });
  const [zoom, setZoom] = useState(100);
  const [form, setForm] = useState<FormKind | null>(null);
  const [liveForm, setLiveForm] = useState<LiveFormKind | null>(null);
  const [moduleForm, setModuleForm] = useState<string | null>(null);
  const [prefill, setPrefill] = useState<LivePrefill | undefined>(undefined);
  const [formContext, setFormContext] = useState<{ employeeId: number; name?: string } | undefined>(undefined);
  const [printing, setPrinting] = useState<{ kind: PrintKind; id: number } | null>(null);
  const [backstage, setBackstage] = useState(false);
  // Worksheet design template (src/themes.css), remembered in this browser.
  const [sheetTheme, setSheetTheme] = useState(() => { try { return localStorage.getItem('asco.sheetTheme') || 'teal'; } catch { return 'teal'; } });
  const pickTheme = (t: string) => { setSheetTheme(t); try { localStorage.setItem('asco.sheetTheme', t); } catch { /* storage blocked */ } };
  const [collapsed, setCollapsed] = useState(false);
  const [status, setStatus] = useState<{ msg: string; kind: 'ok' | 'err' } | null>(null);
  const [q, setQ] = useState('');
  const [qOpen, setQOpen] = useState(false);
  const statusTimer = useRef<number | undefined>(undefined);

  const sess = useSession();
  const live = sess.mode === 'live';
  const grant = live ? sess.me!.companies.find((c) => c.id === sess.companyId) : undefined;
  const qc = useQueryClient();

  const screen = screenById(active);
  const spec = live ? LIVE[active] : undefined;
  // Per-report date filter (kept while the sheet tab stays open).
  const [reportDates, setReportDates] = useState<Record<string, DateState>>({});
  const dates = reportDates[active] ?? defaultDates();
  const specPath = spec ? datedPath(spec.path, spec.dates, dates) : undefined;
  const blocked = !!spec && ((!!spec.payroll && !grant?.canAccessPayroll) || (!!spec.admin && !grant?.canAdminister));
  const liveQuery = useQuery({
    queryKey: ['sheet', sess.companyId, specPath],
    queryFn: () => api.get<unknown>(specPath!, sess.companyId!),
    enabled: !!spec && !blocked,
  });
  // Industry profile: which module tabs this company sees (demo shows every module as a preview).
  const profile = useQuery({ queryKey: ['sheet', sess.companyId, '/modules/profile'], queryFn: () => api.get<{ industry: string; modules: string[]; jobLabel: string }>('/modules/profile', sess.companyId!), enabled: live });
  const enabledModules = live ? profile.data?.modules ?? [] : ['inventory', 'manufacturing', 'jobs', 'fleet'];
  const screenVisible = (x: Screen) => !x.module || enabledModules.includes(x.module);
  const visibleTabs = TABS.filter((t) => !MODULE_TABS[t] || enabledModules.includes(MODULE_TABS[t]));
  const tabLabel = (t: string) => (t === 'Jobs' ? (profile.data?.industry === 'Logistics' ? 'Logistics' : profile.data?.industry === 'Construction' ? 'Projects' : 'Jobs') : t);
  const lookups = useQuery({ queryKey: ['lookups', sess.companyId], queryFn: () => api.get<Lookups>('/lookups', sess.companyId!), enabled: live, staleTime: 60_000 });
  // Default entry date: today if its period is open, else the nearest open period.
  const entryMode = useEntryMode();
  const lineMode = live && isDocSheet(active) && entryMode === 'lines';
  // Stock on hand for the item badges (only when the Inventory module is on).
  const stockQuery = useQuery({ queryKey: ['sheet', sess.companyId, '/inventory/stock'], queryFn: () => api.get<{ itemCode: string; itemTotal: number }[]>('/inventory/stock', sess.companyId!), enabled: lineMode && enabledModules.includes('inventory') });
  const stockMap = useMemo(() => Object.fromEntries((stockQuery.data ?? []).map((r) => [r.itemCode, r.itemTotal])), [stockQuery.data]);
  const entryDate = useMemo(() => {
    const t0 = new Date().toISOString().slice(0, 10);
    const open = lookups.data?.openPeriods ?? [];
    if (!open.length || open.some((p) => t0 >= p.startDate && t0 <= p.endDate)) return t0;
    const last = open[open.length - 1];
    return last.endDate < t0 ? last.endDate : last.startDate;
  }, [lookups.data]);
  // Unpaid invoices (receipt batch) or bills (payment batch) for the "against" column.
  const openPath = openDocsPath(active);
  const openDocsQuery = useQuery({
    queryKey: ['sheet', sess.companyId, openPath],
    queryFn: () => api.get<unknown>(openPath!, sess.companyId!),
    enabled: live && !!openPath,
  });
  const openDocs = useMemo(() => toOpenDocs(active, openDocsQuery.data), [active, openDocsQuery.data]);
  // Accounting tool sheets (cost-centre P&L, gratuity, depreciation & prepayment schedules).
  const toolVersion = useToolStores();
  const assets = useAssets(live && active === 'dep-schedule', sess.companyId);
  const tPath = live && isToolSheet(active) ? toolPath(active, entryDate, assets) : undefined;
  const toolQuery = useQuery({ queryKey: ['sheet', sess.companyId, tPath], queryFn: () => api.get<unknown>(tPath!, sess.companyId!), enabled: !!tPath });
  const resolver: AccountResolver = useMemo(() => {
    if (!live) return demoResolver(s);
    const byCode = new Map((lookups.data?.accounts ?? []).map((a) => [a.code, a]));
    return (code: string) => { const a = byCode.get(code); return a ? { id: a.id, name: a.name, isPostable: true } : undefined; };
  }, [live, s, lookups.data]);
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
  const view = useMemo((): SheetView => {
    if (screen.kind === 'entry' && isEntrySheet(active)) {
      if (!live) return { columns: [{ key: 'msg', label: screen.label, width: 620 }] as Col[], rows: [{ msg: 'Sign in to a live company to use fast entry — it posts straight to the books.', _meta: { style: 'muted' } }] as Row[] };
      return entryView(active, lookups.data, openDocs, entryDate);
    }
    if (screen.kind === 'tool' && isToolSheet(active)) {
      if (!live) return { columns: [{ key: 'msg', label: screen.label, width: 620 }] as Col[], rows: [{ msg: `Sign in to a live company to use ${screen.label} — it works on the real books.`, _meta: { style: 'muted' } }] as Row[] };
      return toolView(active, tPath ? toolQuery.data : undefined, { pending: !!tPath && toolQuery.isPending, error: tPath && toolQuery.isError ? toolQuery.error.message : undefined });
    }
    if (screen.kind === 'journal-entry') return { columns: JE_COLUMNS, rows: jeRows(resolver, draft), editable: jeEditable, onEdit: jeEdit };
    if (screen.kind === 'ai' || (live && screen.kind === 'scan')) return { columns: [] as Col[], rows: [] as Row[] };
    const notice = (msg: string, style: RowMeta['style'] = 'muted') => ({ columns: [{ key: 'msg', label: screen.label, width: 620 }] as Col[], rows: [{ msg, _meta: { style } }] as Row[] });
    const base = (() => {
      if (!live) return { columns: screen.columns ?? [], rows: screen.rows?.(s) ?? [] };
      if (active === 'companies') return {
        columns: [{ key: 'name', label: 'Company', width: 260 }, { key: 'code', label: 'Code', width: 90 }, { key: 'role', label: 'Your Role', width: 110 }, { key: 'subscription', label: 'Subscription', width: 110 }] as Col[],
        rows: sess.me!.companies.map((c) => ({ name: c.name, code: c.code, role: c.role, subscription: c.subscription })) as Row[],
      };
      if (!spec) return notice(`${screen.label} is not connected to the API yet — ${screen.planned ? 'planned module' : 'arrives in Phase 2 with posting / editing'}.`);
      if (blocked) return notice(spec.payroll ? 'You need the payroll grant for this company to see HR & payroll data.' : 'Company administrator access is required.', 'warn');
      if (liveQuery.isPending) return notice('Loading…');
      if (liveQuery.isError) return notice(liveQuery.error.message, 'warn');
      const data = filterByDate(liveQuery.data, spec.dates, dates);
      const rows = (spec.rows ? spec.rows(data, dates) : data) as Row[];
      const hasData = rows.some((r) => !(r._meta as RowMeta | undefined)?.style?.match(/total|grand/));
      return { columns: spec.columns, rows: hasData ? rows : [{ [spec.columns[0].key]: 'No records in this company yet.', _meta: { style: 'muted' } } as Row] };
    })();
    let rows = base.rows;
    const columns = base.columns;
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
  }, [screen, s, draft, filter, sorts, active, live, spec, blocked, liveQuery.data, liveQuery.status, liveQuery.error, dates, sess.me, resolver, entryVersion, lookups.data, openDocs, entryDate, toolVersion, tPath, toolQuery.data, toolQuery.status, toolQuery.error]);

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
    a.download = `${[screen.label, live && spec?.dates ? dateLabel(spec.dates, dates) : ''].filter(Boolean).join(' ').replace(/[^\w]+/g, '-')}.csv`;
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
  const postJv = (asDraft = false) => {
    if (active !== 'journal-voucher') { openSheet('journal-voucher'); return; }
    if (!live) { const r = postDraft(USER); notify(r.msg, r.ok ? 'ok' : 'err'); return; }
    postDraftLive(resolver, sess.companyId!, asDraft).then((r) => { notify(r.msg, r.ok ? 'ok' : 'err'); if (r.ok) qc.invalidateQueries(); });
  };
  /** Posts the fast-entry sheet in view (invoice: post or draft; receipts: every ready row). */
  const postEntry = async (asDraft: boolean) => {
    if (!grant?.canPost) { notify('Your role in this company is read-only', 'err'); return; }
    if (!isEntrySheet(active)) return;
    notify('Posting…');
    const r = await postEntrySheet(active, lookups.data, openDocs, sess.companyId!, entryDate, asDraft);
    notify(r.msg, r.ok ? 'ok' : 'err');
    if (r.msg.startsWith('Posted') || r.msg.startsWith('Saved')) qc.invalidateQueries();
  };
  /** Live command: runs against the API, reports the outcome in the status bar, refreshes every sheet. */
  const liveRun = async (fn: () => Promise<string>) => {
    try { notify(await fn()); await qc.invalidateQueries(); } catch (e) { notify(e instanceof Error ? e.message : String(e), 'err'); }
  };
  const cid = sess.companyId ?? 0;
  const today = new Date().toISOString().slice(0, 10);
  const activeId = typeof activeRow?.id === 'number' ? (activeRow.id as number) : undefined;
  const printSelected = (kind: PrintKind, sheet: string, what: string) => {
    if (active !== sheet) { openSheet(sheet); notify(`Select a ${what} row, then click Print again`); return; }
    if (!activeId) { notify(`Select a ${what} row first`, 'err'); return; }
    setPrinting({ kind, id: activeId });
  };

  /** A date in an open fiscal period: today if open, otherwise the latest open period's last day. */
  const postingDate = () => {
    const open = lookups.data?.openPeriods ?? [];
    if (open.some((p) => today >= p.startDate && today <= p.endDate)) return today;
    const last = open[open.length - 1];
    return last ? (last.endDate < today ? last.endDate : last.startDate) : today;
  };
  const quoteStatus = (status: string) => {
    if (active !== 'estimates' || !activeId) return notify('Select a quotation row on the Estimate / Quotation sheet first', 'err');
    liveRun(async () => { await api.post(`/estimates/${activeId}/status`, { status }, cid); return `Quotation marked ${status}`; });
  };

  // Reports ▾ — every report by category (hides reports of industry modules this company hasn't enabled).
  const reportMenu: MenuItem[] = REPORT_MENU.map((cat) => ({
    label: cat.label, icon: cat.icon, color: cat.color,
    items: cat.items.flatMap((it): MenuItem[] => {
      if (it.sub) { const subs = it.sub.filter((x) => screenVisible(screenById(x.id))); return subs.length ? [{ label: it.label, items: subs.map((x) => ({ label: x.label, run: () => openSheet(x.id) })) }] : []; }
      return screenVisible(screenById(it.id!)) ? [{ label: it.label, run: () => openSheet(it.id!) }] : [];
    }),
  })).filter((c) => c.items!.length);
  const newDoc = (k: FormKind) => ({ needs: 'post' as const, run: () => (live ? setLiveForm(k) : setForm(k)) });
  const extra: Record<string, ActionGroup[]> = {
    Home: [
      { group: 'Clipboard', position: 'start', actions: [
        { label: 'Copy', icon: Copy, color: C.gray, small: true, run: copy, title: 'Copy (Ctrl+C)' },
        { label: 'Export CSV', icon: Download, color: C.gray, small: true, run: toCsv },
        { label: 'Print', icon: Printer, color: C.gray, small: true, run: () => window.print() },
        { label: 'Refresh', icon: RefreshCw, color: C.gray, small: true, run: () => { if (live) { qc.invalidateQueries(); notify('Refreshed from the server'); } else notify('Demo data lives in this browser — nothing to refresh'); } },
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
    Finance: [{ group: 'Post', actions: [{ label: 'Post Voucher', icon: BadgeCheck, color: C.green, needs: 'post', run: () => postJv(), title: 'Post the journal voucher (Ctrl+Enter)' }] }],
    Items: [{ group: 'New', position: 'start', actions: [{ label: 'New Item', icon: PackagePlus, color: C.green, needs: 'post', run: () => setModuleForm('item') }] }],
    Receivables: [
      { group: 'New', actions: [{ label: 'New Invoice', icon: FileText, color: C.green, ...newDoc('sales-invoice') }, { label: 'New Receipt', icon: HandCoins, color: C.green, ...newDoc('receipt') }, { label: 'New Quotation', icon: ClipboardList, color: C.purple, needs: 'post', run: () => setLiveForm('estimate') }, { label: 'New Credit Note', icon: FileMinus, color: C.red, needs: 'post', run: () => setLiveForm('credit-note') }, { label: 'New Customer', icon: UserPlus, color: C.blue, needs: 'post', run: () => setLiveForm('customer') }] },
      { group: 'Documents', actions: [
        { label: 'Print Invoice', icon: Printer, color: C.gray, small: true, needs: 'live', run: () => printSelected('invoice', 'sales-invoices', 'sales invoice') },
        { label: 'Print Receipt', icon: Printer, color: C.gray, small: true, needs: 'live', run: () => printSelected('receipt', 'receipts', 'receipt') },
        { label: 'Print Quotation', icon: Printer, color: C.gray, small: true, needs: 'live', run: () => printSelected('quotation', 'estimates', 'quotation') },
      ] },
      { group: 'Quotation', actions: [
        { label: 'Mark Sent', icon: Send, color: C.blue, small: true, needs: 'post', run: () => quoteStatus('Sent') },
        { label: 'Mark Accepted', icon: BadgeCheck, color: C.green, small: true, needs: 'post', run: () => quoteStatus('Accepted') },
        { label: 'Mark Declined', icon: X, color: C.red, small: true, needs: 'post', run: () => quoteStatus('Declined') },
        { label: 'Convert to Invoice', icon: ArrowRightLeft, color: C.green, needs: 'post', run: () => {
          if (active !== 'estimates' || !activeId) return notify('Select a quotation row on the Estimate / Quotation sheet first', 'err');
          liveRun(async () => { const r = await api.post<{ number: string }>(`/estimates/${activeId}/convert`, { date: postingDate() }, cid); return `Converted to invoice ${r.number} — now posted to the books`; });
        } },
        { label: 'Send Reminder', icon: Mail, color: C.gray, small: true, needs: 'post', run: () => {
          if (active !== 'sales-invoices' || !activeId) return notify('Select an invoice row on the Sales Invoice sheet first', 'err');
          liveRun(async () => { await api.post(`/sales-invoices/${activeId}/remind`, {}, cid); return 'Reminder emailed to the customer (via C-ERP EmailService)'; });
        } },
      ] },
    ],
    Payables: [{ group: 'New', actions: [{ label: 'New Bill', icon: FilePlus, color: C.orange, ...newDoc('purchase-invoice') }, { label: 'New Payment', icon: Banknote, color: C.orange, ...newDoc('payment') }, { label: 'New Expense', icon: Receipt, color: C.red, ...newDoc('expense') }, { label: 'New Debit Note', icon: FileX, color: C.red, needs: 'post', run: () => setLiveForm('debit-note') }, { label: 'New Vendor', icon: Building2, color: C.blue, needs: 'post', run: () => setLiveForm('vendor') }] }],
    Inventory: [
      { group: 'Stock In / Out', actions: [
        { label: 'Stock Receipt', icon: PackagePlus, color: C.green, needs: 'post', run: () => setModuleForm('receipt') },
        { label: 'Stock Issue', icon: PackageMinus, color: C.orange, needs: 'post', run: () => setModuleForm('issue') },
        { label: 'Transfer', icon: ArrowLeftRight, color: C.blue, needs: 'post', run: () => setModuleForm('transfer') },
        { label: 'Adjustment', icon: SlidersHorizontal, color: C.red, needs: 'post', run: () => setModuleForm('adjustment') },
        { label: 'Issue for Invoice', icon: FileOutput, color: C.teal, needs: 'post', run: () => setModuleForm('issue-invoice') },
      ] },
      { group: 'Setup', position: 'end', actions: [
        { label: 'New Warehouse', icon: Warehouse, color: C.gray, small: true, needs: 'post', run: () => setModuleForm('warehouse') },
        { label: 'Reorder Level', icon: SlidersHorizontal, color: C.gray, small: true, needs: 'post', run: () => setModuleForm('reorder') },
      ] },
    ],
    Manufacturing: [{ group: 'Production', actions: [
      { label: 'New BOM', icon: Layers, color: C.purple, needs: 'post', run: () => setModuleForm('bom') },
      { label: 'New Order', icon: Factory, color: C.orange, needs: 'post', run: () => setModuleForm('production-order') },
      { label: 'Complete Order', icon: CircleCheck, color: C.green, needs: 'post', run: () => {
        if (active !== 'production-orders' || !activeId) return notify('Select a released order on the Production Orders sheet first', 'err');
        liveRun(async () => { const r = await api.post<{ orderNo: string; unitCost: number; materialCost: number; conversionCost: number }>(`/manufacturing/orders/${activeId}/complete`, { date: today }, cid); return `Completed ${r.orderNo}: material ${r.materialCost} + conversion ${r.conversionCost} → unit cost ${r.unitCost}`; });
      } },
      { label: 'Cancel Order', icon: CircleX, color: C.red, needs: 'post', run: () => {
        if (active !== 'production-orders' || !activeId) return notify('Select a released order on the Production Orders sheet first', 'err');
        liveRun(async () => { await api.post(`/manufacturing/orders/${activeId}/cancel`, {}, cid); return 'Production order cancelled'; });
      } },
    ] }],
    Jobs: [{ group: 'Operations', actions: [
      { label: 'New Job', icon: Briefcase, color: C.blue, needs: 'post', run: () => setModuleForm('job') },
      { label: 'Job Status', icon: CircleCheck, color: C.teal, needs: 'post', run: () => setModuleForm('job-status') },
      ...(enabledModules.includes('fleet') ? [
        { label: 'New Vehicle', icon: Truck, color: C.orange, needs: 'post' as const, run: () => setModuleForm('vehicle') },
        { label: 'Log Trip', icon: Route, color: C.green, needs: 'post' as const, run: () => setModuleForm('trip') },
      ] : []),
    ] }],
    'CRM & Assets': [{ group: 'Run', actions: [{ label: 'Run Depreciation', icon: Car, color: C.teal, needs: 'post', run: () => (live
      ? liveRun(async () => { const r = await api.post<{ voucher?: string; message?: string }>('/fixed-assets/depreciation', {}, cid); return r.voucher ? `Posted depreciation ${r.voucher}` : r.message ?? 'Nothing to depreciate'; })
      : run('', () => `Posted ${runDepreciation(store.get(), monthEnd(), USER)} for ${monthEnd().slice(0, 7)}`)) }] }],
    'HR & Payroll': [{ group: 'Run', actions: [
      { label: 'Run Payroll', icon: Wallet, color: C.green, needs: 'payroll', run: () => live ? liveRun(async () => {
        const created = await api.post<{ id: number; employees: number }>('/payroll-runs', { runDate: today }, cid);
        const posted = await api.post<{ voucher: string }>(`/payroll-runs/${created.id}/post`, { deductionsAccountId: null }, cid);
        return `Payroll run #${created.id} posted (${created.employees} employees) — voucher ${posted.voucher}; mark paid after the WPS transfer`;
      }) : run('', () => {
        const period = new Date().toLocaleString('en', { month: 'short', year: 'numeric' });
        if (store.get().payrollRuns.some((r) => r.period === period)) throw new PostingError(`Payroll for ${period} already posted.`);
        return `Posted payroll ${postPayroll(store.get(), period, monthEnd(), USER, false)} (${period}) — accrued, not yet paid`;
      }) },
      { label: 'Mark Paid (WPS)', icon: BadgeCheck, color: C.blue, needs: 'payroll', run: () => live ? liveRun(async () => {
        const runs = await api.get<{ id: number; status: string; isPaid: boolean }[]>('/payroll-runs', cid);
        const unpaid = runs.filter((r) => r.status === 'Posted' && !r.isPaid);
        const bank = lookups.data?.bankAccounts[0];
        if (!unpaid.length) throw new Error('No posted, unpaid payroll runs.');
        if (!bank) throw new Error('No bank account found to pay from.');
        for (const r of unpaid) { await api.download(`/payroll-runs/${r.id}/wps`, cid); await api.post(`/payroll-runs/${r.id}/pay`, { bankAccountId: bank.id, paidDate: today }, cid); }
        return `Downloaded WPS SIF and marked ${unpaid.length} run(s) paid from ${bank.name}`;
      }) : run('', () => {
        const unpaid = store.get().payrollRuns.filter((r) => !r.isPaid);
        if (!unpaid.length) throw new PostingError('No unpaid payroll runs.');
        unpaid.forEach((r) => markPayrollPaid(store.get(), r.no, r.runDate, USER));
        return `Paid ${unpaid.length} payroll run(s) via WPS transfer`;
      }) },
    ] }, { group: 'People', actions: [
      { label: 'Approve Leave', icon: BadgeCheck, color: C.green, small: true, needs: 'payroll', run: () => {
        if (active !== 'leave-requests' || !activeId) return notify('Select a request on the Leave Requests sheet first', 'err');
        liveRun(async () => { await api.post(`/leave-requests/${activeId}/decide`, { approved: true }, cid); return `Leave request #${activeId} approved`; });
      } },
      { label: 'Reject Leave', icon: X, color: C.red, small: true, needs: 'payroll', run: () => {
        if (active !== 'leave-requests' || !activeId) return notify('Select a request on the Leave Requests sheet first', 'err');
        liveRun(async () => { await api.post(`/leave-requests/${activeId}/decide`, { approved: false }, cid); return `Leave request #${activeId} rejected`; });
      } },
      { label: 'Portal Access', icon: UserPlus, color: C.purple, small: true, needs: 'admin', run: () => {
        if (active !== 'employees' || !activeId) return notify('Select an employee row on the Employees sheet first', 'err');
        setFormContext({ employeeId: activeId, name: String(activeRow?.fullName ?? '') });
        setLiveForm('portal-access');
      } },
    ] }, { group: 'Documents', actions: [
      { label: 'Payslips', icon: Printer, color: C.gray, small: true, needs: 'live', run: () => printSelected('payslips', 'payroll', 'payroll run') },
      { label: 'WPS File', icon: FileDown, color: C.gray, small: true, needs: 'payroll', run: () => {
        if (active !== 'payroll' || !activeId) return notify('Select a posted payroll run on the Payroll Runs sheet first', 'err');
        liveRun(async () => `Downloaded ${await api.download(`/payroll-runs/${activeId}/wps`, cid)}`);
      } },
    ] }],
    Reports: [{ group: 'Get Report', position: 'start', actions: [{ label: 'Reports', icon: Database, color: C.green, run: () => {}, menu: reportMenu, title: 'All reports by category' }] }, { group: 'Output', actions: [{ label: 'Export CSV', icon: Download, color: C.gray, run: toCsv }, { label: 'Print', icon: Printer, color: C.gray, run: () => window.print() }] }],
    Settings: [{ group: 'Industry', actions: [{ label: 'Configure Industry', icon: Settings2, color: C.teal, needs: 'admin', run: () => setModuleForm('industry') }] }, { group: 'Period End', actions: [
      { label: 'Add Missing Months', icon: CalendarDays, color: C.green, needs: 'admin', run: () => liveRun(async () => {
        const made = await api.post<string[]>('/fiscal-periods/extend', {}, cid);
        return made.length ? `Opened ${made.length} period(s): ${made.join(', ')}` : 'All months up to today already have periods';
      }) },{ label: 'Close Period', icon: CalendarDays, color: C.red, needs: 'admin', run: () => live ? liveRun(async () => {
      const periods = await api.get<{ id: number; name: string; endDate: string; isClosed: boolean }[]>('/fiscal-periods', cid);
      const p = periods.find((x) => !x.isClosed && x.endDate < today);
      if (!p) throw new Error('No completed period left to close.');
      await api.post(`/fiscal-periods/${p.id}/close`, {}, cid);
      return `Closed ${p.name} — C-ERP now rejects postings dated in it`;
    }) : run('', () => {
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
      if (e.ctrlKey && e.key === 'Enter' && live && isEntrySheet(active)) { e.preventDefault(); postEntry(false); }
      if (e.altKey && (e.key === 'q' || e.key === 'Q')) { e.preventDefault(); document.getElementById('tellme')?.focus(); }
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  });

  const results = q.trim()
    ? SCREENS.filter((x) => screenVisible(x) && `${x.label} ${x.tab} ${x.group}`.toLowerCase().includes(q.toLowerCase())).slice(0, 8)
    : [];

  const statusMsg = status?.msg ?? (live && liveQuery.isFetching ? 'Loading…' : editableActive ? 'Enter' : screen.note && active === 'journal-voucher' && !live ? screen.note : 'Ready');
  // Enable each action by what the signed-in user may do in THIS company (same rules as C-ERP).
  const why = (n?: string) => {
    if (!n) return null;
    if (!live) return n === 'live' ? 'Sign in to a live company to use this' : null;
    if (n === 'post' && !grant?.canPost) return 'Your role in this company is read-only';
    if (n === 'payroll' && !(grant?.canPost && grant?.canAccessPayroll)) return 'Needs the payroll grant in this company';
    if (n === 'admin' && !grant?.canAdminister) return 'Needs company administrator access';
    return null;
  };
  const liveOnly = (label: string) => !live && (/^(New Item|New Customer|New Vendor|Add Missing Months|New Quotation|Print Quotation|Mark Sent|Mark Accepted|Mark Declined|Convert to Invoice|New Credit Note|New Debit Note|Send Reminder|WPS File|Approve Leave|Reject Leave|Portal Access|Configure Industry)$/.test(label) || ['Inventory', 'Manufacturing', 'Jobs'].includes(tab));
  const ribbonExtra = (extra[tab] ?? []).map((g) => ({
    ...g,
    actions: g.actions.map((a) => {
      const reason = liveOnly(a.label) ? 'Sign in to a live company to use this' : why(a.needs);
      return reason ? { ...a, disabled: true, title: `${a.label} — ${reason}` } : a;
    }),
  }));
  const companyName = live ? grant?.name ?? '' : s.company.name;
  const userName = live ? sess.me!.displayName : 'Demo user';
  const initials = userName.split(/\s+/).map((w) => w[0]).join('').slice(0, 2).toUpperCase();

  return (
    <div className="app" data-sheet-theme={sheetTheme}>
      <header className="titlebar">
        <div className="tb-left"><div className="logo">A</div></div>
        <div className="tb-title">{companyName} - ASCO</div>
        <div className="tb-right">
          <span className={live ? 'mode-pill live' : 'mode-pill'}>{live ? `Live · ${grant?.role}` : 'Demo data'}</span>
          {live && sess.me!.companies.length > 1 && (
            <select className="tb-company" value={sess.companyId ?? ''} onChange={(e) => sessionActions.switchCompany(Number(e.target.value))} aria-label="Switch company">
              {sess.me!.companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          )}
          <span className="tb-user">{userName}</span><span className="avatar">{initials}</span>
          <button className="tb-signout" title="Sign out" aria-label="Sign out" onClick={() => sessionActions.signOut()}><LogOut size={14} /></button>
        </div>
      </header>

      <nav className="menubar">
        <button className="mb-file" onClick={() => setBackstage(true)}>File</button>
        {visibleTabs.map((t) => (
          <button key={t} className={t === tab ? 'mb-tab on' : 'mb-tab'} onClick={() => { setTab(t); setCollapsed(false); }}>{tabLabel(t)}</button>
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

      {!collapsed && <Ribbon tab={tab} active={active} onOpen={openSheet} extra={ribbonExtra} onCollapse={() => setCollapsed(true)} screenFilter={screenVisible} />}

      <div className="qat">
        <button title="Save (auto-saved)" onClick={() => notify('All changes are posted immediately — nothing to save')}><Save size={16} /></button>
        <button title="Undo — posted vouchers are reversed, not undone" disabled><Undo2 size={16} /></button>
        <button title="Redo" disabled><Redo2 size={16} /></button>
        {collapsed && <button title="Show the Ribbon" onClick={() => setCollapsed(false)}><ChevronUp size={16} style={{ transform: 'rotate(180deg)' }} /></button>}
        <span className="qat-sep" />
        <span className="qat-crumb">{screen.tab} › {screen.group} › <b>{screen.label}</b></span>
      </div>

      {!lineMode && <div className="formulabar">
        <div className="namebox">{screen.kind === 'ai' || (live && screen.kind === 'scan') ? '' : `${colName(sel.c)}${sel.r + 1}`}<ChevronDown size={12} /></div>
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
      </div>}

      <main className="sheet">
        {live && spec?.dates && !blocked && <ReportDates dm={spec.dates} value={dates} onChange={(v) => setReportDates((p) => ({ ...p, [active]: v }))} />}
        {live && isToolSheet(active) && <ToolHeader id={active} L={lookups.data} data={toolQuery.data} assets={assets} companyId={sess.companyId!} today={entryDate} canPost={!!grant?.canPost} notify={notify} />}
        {live && isEntrySheet(active) && <EntryHeader id={active} L={lookups.data} today={entryDate} ready={view.ready ?? 0} onPost={(d) => postEntry(d)} />}
        {screen.kind === 'journal-entry' && <JeHeader onPost={() => postJv()} onDraft={live ? () => postJv(true) : undefined} hint={live ? 'Type account codes from the Chart of Accounts sheet · Post, or save a draft for approval' : undefined} />}
        {screen.kind === 'ai' ? (
          <AiPanel companyId={live ? cid : undefined} />
        ) : live && screen.kind === 'scan' ? (
          <ScanBill companyId={cid} onUse={(p) => { setPrefill(p); setLiveForm('purchase-invoice'); }} />
        ) : lineMode && isDocSheet(active) ? (
          <LineEditor id={active} L={lookups.data} stock={stockMap} onNotice={notify} />
        ) : (
          <Grid columns={view.columns} rows={view.rows} sel={sel} onSel={setSel} zoom={zoom} editable={view.editable} onEdit={view.onEdit} optionsFor={view.optionsFor} onLink={live ? (kind, id) => setPrinting({ kind, id }) : undefined}
            onGrow={live && isEntrySheet(active) ? (upto) => growEntrySheet(active, upto) : undefined}
            onPasted={(filled, skipped) => notify(filled ? `Pasted ${filled} cell${filled === 1 ? '' : 's'}${skipped ? ` · ${skipped} skipped (calculated or read-only columns)` : ''}` : 'Nothing pasted — these cells are calculated or read-only', filled ? 'ok' : 'err')} />
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
        <select className="sb-theme" value={sheetTheme} onChange={(e) => pickTheme(e.target.value)} aria-label="Sheet design" title="Sheet design">
          <option value="teal">Design: Ledger Teal</option>
          <option value="paper">Design: Account Book</option>
          <option value="slate">Design: Slate</option>
          <option value="midnight">Design: Midnight</option>
        </select>
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
      {liveForm && <LiveDocForm kind={liveForm} companyId={cid} context={formContext} prefill={prefill} onClose={() => { setLiveForm(null); setPrefill(undefined); }} onDone={(msg) => { const t = LIVE_TARGET[liveForm]; setLiveForm(null); setPrefill(undefined); openSheet(t); notify(msg); }} />}
      {moduleForm && <ModuleForm spec={MODULE_FORMS[moduleForm]} companyId={cid} onClose={() => setModuleForm(null)} onDone={(msg, target) => { setModuleForm(null); if (target) openSheet(target); notify(msg); }} />}
      {printing && <PrintDoc kind={printing.kind} id={printing.id} companyId={cid} onClose={() => setPrinting(null)} />}
      {backstage && <Backstage live={live ? { company: companyName, role: grant?.role ?? '', user: sess.me!.email } : undefined} onClose={() => setBackstage(false)} onExport={toCsv} />}
    </div>
  );
}
