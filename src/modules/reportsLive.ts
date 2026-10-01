// Live specs for the extra reports in the Reports ▾ menu (ids rpt-*). Some read ASCO's
// /reports/* endpoints (ReportEndpoints.cs); the rest regroup lists C-ERP already serves.
// Ranges are the calendar year to date unless the endpoint says otherwise.
import type { Col, Row, RowMeta } from './registry';
import type { LiveSpec } from './live';

type J = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

const t = (key: string, label: string, width = 120): Col => ({ key, label, width });
const m = (key: string, label: string, width = 110): Col => ({ key, label, width, type: 'money' });
const d = (key: string, label: string, width = 96): Col => ({ key, label, width, type: 'date' });
const n = (key: string, label: string, width = 70): Col => ({ key, label, width, type: 'number' });
const meta = (style?: RowMeta['style'], indent?: number, formula?: boolean) => ({ _meta: { style, indent, formula: formula ? {} : undefined } as RowMeta });
const r2 = (x: number) => Math.round(x * 100) / 100;
const sum = (rows: J[], k: string) => r2(rows.reduce((a, r) => a + (Number(r[k]) || 0), 0));
const pct = (a: number, b: number) => (b ? `${((a / b) * 100).toFixed(1)}%` : '');
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function totalRow(rows: J[], labelKey: string, keys: string[], label = 'Total', style: RowMeta['style'] = 'grand'): Row {
  const r: Row = { [labelKey]: label, ...meta(style, undefined, true) };
  for (const k of keys) r[k] = sum(rows, k);
  return r;
}

/** Group rows by a key; each group gets a header, its rows indented, and a subtotal. */
function grouped(rows: J[], keyOf: (x: J) => string, order: string[] | null, labelKey: string, keys: string[]): Row[] {
  const groups = new Map<string, J[]>();
  for (const x of rows) { const k = keyOf(x); groups.set(k, [...(groups.get(k) ?? []), x]); }
  const names = order ? order.filter((o) => groups.has(o)) : [...groups.keys()];
  return names.flatMap((g) => {
    const xs = groups.get(g)!;
    return [{ [labelKey]: `${g} (${xs.length})`, ...meta('group') }, ...xs.map((x) => ({ ...x, ...meta(undefined, 1) })), totalRow(xs, labelKey, keys, `Total ${g}`, 'total')] as Row[];
  });
}

const BUCKETS = ['Current (not yet due)', '1–30 days overdue', '31–60 days overdue', '61–90 days overdue', 'Over 90 days overdue'];
const bucket = (days: number) => (days <= 0 ? BUCKETS[0] : days <= 30 ? BUCKETS[1] : days <= 60 ? BUCKETS[2] : days <= 90 ? BUCKETS[3] : BUCKETS[4]);
const daysPast = (iso: string) => Math.floor((Date.now() - Date.parse(`${iso}T00:00:00`)) / 86_400_000);

/** Sum documents per key (salesperson, month…), skipping drafts and voids. */
function rollup(xs: J[], keyOf: (x: J) => string, labelKey: string) {
  const live = xs.filter((x) => !/draft|void/i.test(String(x.status)));
  const map = new Map<string, J>();
  for (const x of live) {
    const k = keyOf(x);
    const a = map.get(k) ?? { [labelKey]: k, count: 0, net: 0, vat: 0, gross: 0, balance: 0 };
    a.count++; a.net += x.net ?? 0; a.vat += x.vat ?? 0; a.gross += x.gross ?? 0; a.balance += x.balance ?? 0;
    map.set(k, a);
  }
  return [...map.values()].map((a): J => ({ ...a, net: r2(a.net), vat: r2(a.vat), gross: r2(a.gross), balance: r2(a.balance) }));
}

export const REPORT_LIVE: Record<string, LiveSpec> = {
  // ── Business overview ──────────────────────────────────────────────────
  'rpt-monthly-pnl': {
    path: '/reports/monthly-pnl',
    columns: [t('code', 'Account', 70), t('name', 'Particulars', 220), ...MONTHS.map((mo, i) => m(`m${i + 1}`, mo, 92)), m('total', 'Total', 120)],
    rows: (x: J) => {
      const line = (r: J): Row => { const o: Row = { code: r.code, name: r.name, ...meta(undefined, 1) }; r.months.forEach((v: number, i: number) => { if (v) o[`m${i + 1}`] = r2(v); }); o.total = r2(r.months.reduce((a: number, b: number) => a + b, 0)); return o; };
      const tot = (rs: J[], name: string, style: RowMeta['style']): Row => { const o: Row = { name, ...meta(style, undefined, true) }; for (let i = 0; i < 12; i++) o[`m${i + 1}`] = r2(rs.reduce((a, r) => a + r.months[i], 0)); o.total = r2(rs.reduce((a, r) => a + r.months.reduce((p: number, q: number) => p + q, 0), 0)); return o; };
      const inc = tot(x.income, 'Total income', 'total'), exp = tot(x.expense, 'Total expenses', 'total');
      const net: Row = { name: `Net profit / (loss) — ${x.year}`, ...meta('grand') };
      for (const k of [...MONTHS.map((_, i) => `m${i + 1}`), 'total']) net[k] = r2((inc[k] as number) - (exp[k] as number));
      return [{ name: 'Income', ...meta('group') }, ...x.income.map(line), inc, { name: 'Expenses', ...meta('group') }, ...x.expense.map(line), exp, net];
    },
  },
  'rpt-ratios': {
    path: '/reports/ratios',
    columns: [t('name', 'Measure', 260), t('value', 'Value', 150), t('note', 'How it is worked out', 420)],
    rows: (x: J) => {
      const fmt = (v: number | null, unit: string) => (v == null ? 'n/a' : unit === '%' ? `${v.toFixed(1)} %` : unit === 'x' ? `${v.toFixed(2)} x` : unit === 'days' ? `${v} days` : `AED ${v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`);
      const out: Row[] = [];
      let g = '';
      for (const r of x.rows as J[]) {
        if (r.group !== g) { g = r.group; out.push({ name: g, ...meta('group') }); }
        out.push({ name: r.name, value: fmt(r.value, r.unit), note: r.note, ...meta(undefined, 1) });
      }
      out.push({ name: `Period ${x.from} to ${x.to}`, ...meta('muted') });
      return out;
    },
  },
  'rpt-equity': {
    path: '/reports/equity-movement',
    columns: [t('code', 'Account', 80), t('name', 'Equity', 280), m('opening', 'Opening', 130), m('increase', 'Increase', 120), m('decrease', 'Decrease', 120), m('closing', 'Closing', 130)],
    rows: (x: J) => {
      const e = x.earnings;
      const earn = { name: 'Retained & current-year earnings (not yet closed)', opening: r2(e.opening), increase: e.profit > 0 ? r2(e.profit) : 0, decrease: e.profit < 0 ? r2(-e.profit) : 0, closing: r2(e.closing), ...meta(undefined, 1) };
      const rows = [...(x.equity as J[]).map((r) => ({ ...r, ...meta(undefined, 1) })), earn];
      return [{ name: `Movement of equity — ${x.from} to ${x.to}`, ...meta('group') }, ...rows, totalRow(rows, 'name', ['opening', 'increase', 'decrease', 'closing'], 'Total equity')];
    },
  },

  // ── Sales ─────────────────────────────────────────────────────────────
  'rpt-sales-by-customer': {
    path: '/reports/customer-revenue',
    columns: [t('code', 'Code', 80), t('name', 'Customer', 260), n('documentCount', 'Invoices', 80), m('amount', 'Sales (net)', 130), t('share', 'Share', 70)],
    rows: (xs: J[]) => { const tot = sum(xs, 'amount'); return [...xs.map((x) => ({ ...x, share: pct(x.amount, tot) })), totalRow(xs, 'name', ['amount', 'documentCount'])]; },
  },
  'rpt-sales-by-item': {
    path: '/reports/sales-by-item',
    columns: [t('item', 'Item / Service', 300), n('invoices', 'Invoices', 80), n('quantity', 'Qty Sold', 80), m('net', 'Amount (net)', 130), m('vat', 'VAT', 100), m('gross', 'Total', 130), t('share', 'Share', 70)],
    rows: (x: J) => { const tot = sum(x.rows, 'net'); return [...x.rows.map((r: J) => ({ ...r, share: pct(r.net, tot) })), totalRow(x.rows, 'item', ['quantity', 'net', 'vat', 'gross'], `Total ${x.from} to ${x.to}`)]; },
  },
  'rpt-sales-by-salesperson': {
    path: '/sales-invoices',
    columns: [t('salesperson', 'Sales Person', 220), n('count', 'Invoices', 80), m('net', 'Net Sales', 130), m('vat', 'VAT', 100), m('gross', 'Total', 130), m('balance', 'Still Outstanding', 140)],
    rows: (xs: J[]) => { const rs = rollup(xs, (x) => x.salesperson || '(not assigned)', 'salesperson').sort((a, b) => b.net - a.net); return [...rs, totalRow(rs, 'salesperson', ['count', 'net', 'vat', 'gross', 'balance'])]; },
  },
  'rpt-sales-summary': {
    path: '/sales-invoices',
    columns: [t('month', 'Month', 120), n('count', 'Invoices', 80), m('net', 'Net Sales', 130), m('vat', 'VAT', 100), m('gross', 'Total', 130), m('balance', 'Still Outstanding', 140)],
    rows: (xs: J[]) => { const rs = rollup(xs, (x) => String(x.date).slice(0, 7), 'month').sort((a, b) => String(a.month).localeCompare(String(b.month))); return [...rs, totalRow(rs, 'month', ['count', 'net', 'vat', 'gross', 'balance'])]; },
  },

  // ── Receivables / payables ────────────────────────────────────────────
  'rpt-customer-balances': {
    path: '/customers',
    columns: [t('code', 'Code', 80), t('name', 'Customer', 260), m('invoiced', 'Invoiced', 130), m('received', 'Received', 130), m('outstanding', 'Balance', 130), m('creditLimit', 'Credit Limit', 120)],
    rows: (xs: J[]) => { const rs = xs.filter((x) => x.invoiced || x.received || x.outstanding).map((x) => ({ ...x, ...(x.creditLimit && x.outstanding > x.creditLimit ? meta('warn') : {}) })); return [...rs, totalRow(rs, 'name', ['invoiced', 'received', 'outstanding'])]; },
  },
  'rpt-ar-aging-details': {
    path: '/outstanding-invoices',
    columns: [t('invoiceNo', 'Invoice No', 140), d('date', 'Date'), d('dueDate', 'Due Date'), t('customerName', 'Customer', 240), n('daysOverdue', 'Days Overdue', 100), m('amountDue', 'Balance Due', 130), t('employeeName', 'Sales Person', 140)],
    rows: (xs: J[]) => [...grouped(xs, (x) => bucket(x.daysOverdue), BUCKETS, 'invoiceNo', ['amountDue']), totalRow(xs, 'invoiceNo', ['amountDue'], 'Total receivable')],
  },
  'rpt-vendor-balances': {
    path: '/vendors',
    columns: [t('code', 'Code', 80), t('name', 'Vendor', 260), m('billed', 'Billed', 130), m('paid', 'Paid', 130), m('outstanding', 'Balance', 130)],
    rows: (xs: J[]) => { const rs = xs.filter((x) => x.billed || x.paid || x.outstanding); return [...rs, totalRow(rs, 'name', ['billed', 'paid', 'outstanding'])]; },
  },
  'rpt-ap-aging-details': {
    path: '/purchase-invoices',
    columns: [t('invoiceNo', 'Bill No', 140), d('date', 'Date'), d('dueDate', 'Due Date'), t('vendorName', 'Vendor', 240), n('daysOverdue', 'Days Overdue', 100), m('balance', 'Balance Due', 130)],
    rows: (xs: J[]) => { const open = xs.filter((x) => x.balance > 0).map((x) => ({ ...x, daysOverdue: Math.max(0, daysPast(x.dueDate)) })); return [...grouped(open, (x) => bucket(daysPast(x.dueDate)), BUCKETS, 'invoiceNo', ['balance']), totalRow(open, 'invoiceNo', ['balance'], 'Total payable')]; },
  },

  // ── Purchases & expenses ──────────────────────────────────────────────
  'rpt-purchases-by-vendor': {
    path: '/reports/vendor-spend',
    columns: [t('code', 'Code', 80), t('name', 'Vendor', 260), n('documentCount', 'Documents', 90), m('amount', 'Purchases (net)', 140), t('share', 'Share', 70)],
    rows: (xs: J[]) => { const tot = sum(xs, 'amount'); return [...xs.map((x) => ({ ...x, share: pct(x.amount, tot) })), totalRow(xs, 'name', ['amount', 'documentCount'])]; },
  },
  'rpt-purchases-by-item': {
    path: '/reports/purchases-by-item',
    columns: [t('item', 'Item / Description', 260), t('account', 'Account', 220), n('documents', 'Lines', 70), n('quantity', 'Qty', 70), m('net', 'Amount (net)', 130), m('vat', 'VAT', 100), m('gross', 'Total', 130)],
    rows: (x: J) => [...x.rows, totalRow(x.rows, 'item', ['net', 'vat', 'gross'], `Total ${x.from} to ${x.to}`)],
  },
  'rpt-expenses-by-category': {
    path: '/reports/profit-and-loss',
    columns: [t('code', 'Account', 80), t('name', 'Expense Category', 280), m('period', 'Amount', 140), t('share', 'Share', 80)],
    rows: (x: J) => {
      const sec: [string, J[]][] = [['Cost of goods sold', x.costOfGoodsSold], ['Operating expenses', x.operatingExpense], ['Non-operating expenses', x.nonOperatingExpense]];
      const all = sec.flatMap(([, ls]) => ls);
      const tot = sum(all, 'period');
      return [...sec.filter(([, ls]) => ls.length).flatMap(([title, ls]) => [{ name: title, ...meta('group') }, ...ls.map((l) => ({ code: l.code, name: l.name, period: l.period, share: pct(l.period, tot), ...meta(undefined, 1) })), totalRow(ls, 'name', ['period'], `Total ${title.toLowerCase()}`, 'total')] as Row[]),
        { ...totalRow(all, 'name', ['period'], `Total expenses — ${x.periodName}`), share: '100%' }];
    },
  },

  // ── Taxes / banking / accountant / payroll ────────────────────────────
  'rpt-vat-return': {
    path: '/reports/vat-return',
    columns: [t('box', 'Box', 60), t('name', 'Description', 440), m('amount', 'Amount (AED)', 150), m('vat', 'VAT (AED)', 150)],
    rows: (x: J) => [...(x.rows as J[]).map((r) => ({ box: r.box, name: r.name, amount: r.style === 'group' || (r.amount === 0 && r.vat != null && ['12', '13', '14'].includes(r.box)) ? undefined : r.amount, vat: r.vat ?? undefined, ...meta(r.style ?? undefined, r.style ? undefined : 1) })),
      { name: `Tax period ${x.from} to ${x.to} — from posted invoices, bills, expenses and credit/debit notes`, ...meta('muted') }],
  },
  'rpt-bank-balances': {
    path: '/dashboard',
    columns: [t('code', 'Account', 90), t('name', 'Bank / Cash Account', 300), m('balance', 'Balance', 150)],
    rows: (x: J) => [...x.cash, totalRow(x.cash, 'name', ['balance'], 'Total cash & bank')],
  },
  'rpt-bank-book': {
    path: '/reports/bank-book',
    columns: [d('date', 'Date'), t('voucherNo', 'Voucher', 120), t('type', 'Type', 80), t('narration', 'Narration', 320), t('costCenter', 'Cost Ctr', 80), m('debit', 'Receipts (Dr)', 120), m('credit', 'Payments (Cr)', 120), m('runningBalance', 'Balance', 130)],
    rows: (x: J) => (x.books as J[]).flatMap((b) => [
      { narration: `${b.code} · ${b.name}`, ...meta('group') },
      { narration: `Opening balance ${x.from}`, runningBalance: b.opening, ...meta('muted', 1) },
      ...(b.rows as J[]).map((r) => ({ ...r, ...meta(undefined, 1) })),
      { narration: `Closing balance ${x.to}`, debit: b.totalDebit, credit: b.totalCredit, runningBalance: b.closing, ...meta('total') },
    ] as Row[]),
  },
  'rpt-account-type-summary': {
    path: '/reports/trial-balance',
    columns: [t('type', 'Account Type', 160), n('accounts', 'Accounts', 80), m('debit', 'Closing Debit', 140), m('credit', 'Closing Credit', 140), m('net', 'Net Balance', 140)],
    rows: (x: J) => {
      const order = ['Asset', 'Liability', 'Equity', 'Income', 'Expense'];
      const rs = order.map((ty) => {
        const xs = (x.rows as J[]).filter((r) => String(r.type) === ty && (r.closingDebit || r.closingCredit));
        return { type: ty, accounts: xs.length, debit: sum(xs, 'closingDebit'), credit: sum(xs, 'closingCredit'), net: r2(sum(xs, 'closingDebit') - sum(xs, 'closingCredit')) };
      }).filter((r) => r.accounts);
      return [...rs, { ...totalRow(rs, 'type', ['accounts', 'debit', 'credit', 'net'], `Total (${x.rangeLabel})`) }];
    },
  },
  'rpt-expiring-docs': {
    path: '/expiring-documents', payroll: true,
    columns: [t('employeeCode', 'Code', 80), t('employeeName', 'Employee', 220), t('documentType', 'Document', 160), d('expiryDate', 'Expires'), n('daysLeft', 'Days Left', 90)],
    rows: (xs: J[]) => xs.map((x) => { const left = -daysPast(x.expiryDate); return { ...x, daysLeft: left, ...(left < 30 ? meta('warn') : {}) }; }),
  },
};
