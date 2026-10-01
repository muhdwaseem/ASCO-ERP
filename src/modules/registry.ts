// Every C-ERP screen (NavMenu.razor) mapped onto an Excel-style ribbon tab + group.
// A screen is a "sheet": columns + rows computed from the ledger. Adding a module = adding one entry.

import type { LucideIcon } from 'lucide-react';
import {
  LayoutDashboard, Sparkles, BookOpen, Scale, BookText, NotebookPen, ScrollText, Users, BadgeDollarSign, FileText, Repeat, ClipboardList, Truck, HandCoins, FileMinus, Clock, ListChecks, ArrowLeftRight, Building2, FilePlus, Banknote, FileX, Receipt, Coins, Package, Ruler, Tags, Boxes, Layers, UserPlus, Car, IdCard, Wallet, FileSpreadsheet, TrendingUp, ChartPie, Waves, ChartColumn, Percent, History, Building, CalendarDays, Network, Landmark, Calculator, Wand2, Split, ShieldCheck, UserCog, ScanLine, TriangleAlert, CircleUser, Warehouse, Factory, Ship, Fuel, Briefcase, ClipboardCheck, Blocks, SlidersHorizontal, Route, CalendarClock, PackagePlus, CalendarRange, Grid3x3,
} from 'lucide-react';
import type { LedgerState } from '../engine/types';
import { d, m, n, t } from './sheetKit';
import {
  ACC, aging, accountName, balances, balanceSheet, billOutstanding, cashFlow, docTotals, generalLedger, invoiceOutstanding,
  monthlyDepreciation, naturalBalance, partyName, payrollTotals, pnl, r2, trialBalance,
} from '../engine/ledger';

export type ColType = 'text' | 'money' | 'date' | 'number' | 'pct';
export interface Col { key: string; label: string; width?: number; type?: ColType }
export type Cell = string | number | undefined;
export type Row = { [k: string]: Cell | RowMeta | undefined; _meta?: RowMeta };
export interface RowMeta { style?: 'group' | 'total' | 'grand' | 'muted' | 'warn'; indent?: number; formula?: Record<string, string> }
export type FormKind = 'sales-invoice' | 'purchase-invoice' | 'receipt' | 'payment' | 'expense';

export interface Screen {
  id: string;
  label: string;
  tab: string;
  group: string;
  icon: LucideIcon;
  color: string;
  kind?: 'sheet' | 'journal-entry' | 'ai' | 'scan' | 'entry' | 'tool';
  /** Opened from the Reports ▾ menu only — no ribbon button of its own. */
  menuOnly?: boolean;
  columns?: Col[];
  rows?: (s: LedgerState) => Row[];
  newForm?: FormKind;
  note?: string; // shown in the status bar — e.g. what's planned
  planned?: boolean;
  /** Industry module the screen belongs to — its tab only shows when the company enables it. */
  module?: 'inventory' | 'manufacturing' | 'jobs' | 'fleet';
}

export const TODAY = new Date().toISOString().slice(0, 10);
export const FY_START = `${TODAY.slice(0, 4)}-01-01`;

const C = { blue: '#4f9bea', green: '#33b36b', teal: '#2bb3a8', orange: '#f0a33a', red: '#e5534b', purple: '#a57be8', gold: '#e2c044', gray: '#a0a0a0' };


const meta = (style: RowMeta['style'], extra: Partial<RowMeta> = {}): { _meta: RowMeta } => ({ _meta: { style, ...extra } });

function totalRow(rows: Row[], label: string, labelKey: string, keys: string[], style: RowMeta['style'] = 'total'): Row {
  const r: Row = { [labelKey]: label, ...meta(style, { formula: {} }) };
  for (const k of keys) r[k] = r2(rows.reduce((acc, x) => acc + (typeof x[k] === 'number' ? (x[k] as number) : 0), 0));
  return r;
}

function invoiceStatus(s: LedgerState, no: string, dueDate: string, gross: number) {
  const out = invoiceOutstanding(s, no);
  if (out <= 0) return 'Paid';
  if (dueDate < TODAY) return 'Overdue';
  return out < gross ? 'Partially Paid' : 'Open';
}

function billStatus(s: LedgerState, no: string, dueDate: string, gross: number) {
  const out = billOutstanding(s, no);
  if (out <= 0) return 'Paid';
  if (dueDate < TODAY) return 'Overdue';
  return out < gross ? 'Partially Paid' : 'Open';
}

function partyBalance(s: LedgerState, control: string, party: string) {
  return r2(s.vouchers.flatMap((v) => v.lines).filter((l) => l.account === control && l.party === party).reduce((acc, l) => acc + l.debit - l.credit, 0));
}

// UAE EOSB (gratuity): 21 days' basic per year for the first 5 years, 30 days' after.
export function gratuity(basic: number, joining: string, asOf = TODAY) {
  const years = (Date.parse(asOf) - Date.parse(joining)) / (365.25 * 86_400_000);
  if (years < 1) return 0;
  const daily = basic / 30;
  const first = Math.min(years, 5) * 21 * daily;
  const rest = Math.max(years - 5, 0) * 30 * daily;
  return r2(Math.min(first + rest, basic * 24));
}

function accumulatedDep(s: LedgerState, assetCode: string) {
  return r2(s.vouchers.flatMap((v) => v.lines).filter((l) => l.account === ACC.ACC_DEP && l.narration === assetCode).reduce((a, l) => a + l.credit - l.debit, 0));
}

// ---------------------------------------------------------------- screens

export const SCREENS: Screen[] = [
  // HOME
  {
    id: 'dashboard', label: 'Dashboard', tab: 'Home', group: 'Workbook', icon: LayoutDashboard, color: C.blue,
    columns: [t('metric', 'Metric', 260), m('value', 'Value (AED)', 150), t('note', 'Note', 360)],
    rows: (s) => {
      const p = pnl(s, FY_START, TODAY);
      const b = balances(s);
      const bal = (code: string) => (b.has(code) ? naturalBalance(s, code, b.get(code)!) : 0);
      const ar = aging(s, 'AR', TODAY);
      const ap = aging(s, 'AP', TODAY);
      const overdue = r2(ar.reduce((a, r) => a + r.d30 + r.d60 + r.d90 + r.d90p, 0));
      const visas = s.employees.filter((e) => e.visaExpiryDate <= new Date(Date.now() + 60 * 86_400_000).toISOString().slice(0, 10));
      return [
        { metric: 'PROFIT & LOSS — YEAR TO DATE', ...meta('group') },
        { metric: 'Revenue', value: p.totalRev, note: `${s.salesInvoices.length} invoices posted` },
        { metric: 'Expenses', value: p.totalExp, note: 'incl. payroll & depreciation' },
        { metric: 'Net profit', value: p.net, note: p.totalRev ? `${((p.net / p.totalRev) * 100).toFixed(1)}% net margin` : '', ...meta('total') },
        { metric: 'CASH & WORKING CAPITAL', ...meta('group') },
        { metric: 'Cash & bank', value: r2(bal(ACC.BANK) + bal(ACC.PETTY)), note: 'Emirates Bank + petty cash' },
        { metric: 'Receivables outstanding', value: r2(ar.reduce((a, r) => a + r.total, 0)), note: `${ar.length} customers with open balances` },
        { metric: 'Receivables overdue', value: overdue, note: overdue > 0 ? 'Chase in AR Aging' : 'All current', ...(overdue > 0 ? meta('warn') : {}) },
        { metric: 'Payables outstanding', value: r2(ap.reduce((a, r) => a + r.total, 0)), note: `${ap.length} vendors to pay` },
        { metric: 'Net VAT payable', value: r2(bal(ACC.VAT_OUT) - bal(ACC.VAT_IN)), note: 'Output VAT − input VAT (FTA return)' },
        { metric: 'PEOPLE & PIPELINE', ...meta('group') },
        { metric: 'Monthly payroll (gross)', value: payrollTotals(s, { employees: s.employees.filter((e) => e.status === 'Active').map((e) => e.employeeCode) }).gross, note: `${s.employees.length} active employees` },
        { metric: 'Open sales pipeline', value: s.leads.filter((l) => !['Won', 'Lost'].includes(l.stage)).reduce((a, l) => a + l.estimatedValue, 0), note: `${s.leads.length} leads` },
        { metric: 'Visa expiries in next 60 days', value: String(visas.length), note: visas.map((e) => e.fullName).join(', ') || 'None', ...(visas.length ? meta('warn') : {}) },
      ];
    },
  },

  // FINANCE
  {
    id: 'chart-of-accounts', label: 'Chart of Accounts', tab: 'Finance', group: 'Ledger', icon: BookOpen, color: C.blue,
    columns: [t('code', 'Account No', 90), t('name', 'Account Name', 260), t('type', 'Type', 80), t('postable', 'Postable', 76), t('parent', 'Parent', 70), t('category', 'Category', 150), t('pnl', 'P&L Section', 150), t('ccy', 'Ccy', 50), m('balance', 'Balance')],
    rows: (s) => {
      const b = balances(s);
      return s.accounts.map((a) => {
        const bal = a.isPostable ? (b.has(a.code) ? naturalBalance(s, a.code, b.get(a.code)!) : 0)
          : r2(s.accounts.filter((c) => c.isPostable && c.code.startsWith(a.code.slice(0, a.parent ? 4 : 1))).reduce((acc, c) => acc + (b.has(c.code) ? naturalBalance(s, c.code, b.get(c.code)!) : 0), 0));
        return {
          code: a.code, name: a.name, type: a.type, postable: a.isPostable ? 'Posting' : 'Header', parent: a.parent, category: a.category, pnl: a.pnlSection, ccy: a.currency, balance: bal,
          ...meta(a.isPostable ? undefined : a.parent ? 'total' : 'group', { indent: a.isPostable ? 2 : a.parent ? 1 : 0 }),
        };
      });
    },
  },
  {
    id: 'opening-balances', label: 'Opening Balances', tab: 'Finance', group: 'Ledger', icon: Scale, color: C.teal,
    columns: [t('code', 'Account', 90), t('name', 'Account Name', 260), m('debit', 'Debit'), m('credit', 'Credit')],
    rows: (s) => {
      const rows: Row[] = s.vouchers.filter((v) => v.type === 'OB').flatMap((v) => v.lines).map((l) => ({ code: l.account, name: accountName(s, l.account), debit: l.debit, credit: l.credit }));
      return [...rows, totalRow(rows, 'Total', 'name', ['debit', 'credit'], 'grand')];
    },
  },
  {
    id: 'general-ledger', label: 'General Ledger', tab: 'Finance', group: 'Ledger', icon: BookText, color: C.blue,
    columns: [d('date', 'Date'), t('voucherNo', 'Voucher No', 116), t('account', 'Account', 76), t('name', 'Account Name', 200), t('narration', 'Narration', 240), t('party', 'Party', 170), m('debit', 'Debit'), m('credit', 'Credit'), m('balance', 'Running Bal.')],
    rows: (s) => generalLedger(s),
  },
  {
    id: 'journal-voucher', label: 'Journal Voucher', tab: 'Finance', group: 'Vouchers', icon: NotebookPen, color: C.green, kind: 'journal-entry',
    note: 'Type account codes & amounts, then Post (Ctrl+Enter). Debits must equal credits.',
  },
  {
    id: 'voucher-register', label: 'Voucher Register', tab: 'Finance', group: 'Vouchers', icon: ScrollText, color: C.teal,
    columns: [t('voucherNo', 'Voucher No', 120), t('type', 'Type', 56), d('date', 'Date'), t('narration', 'Narration', 320), n('lines', 'Lines', 56), m('amount', 'Amount'), t('status', 'Status', 70), t('createdBy', 'Created By', 190)],
    rows: (s) => [...s.vouchers].reverse().map((v) => ({ voucherNo: v.voucherNo, type: v.type, date: v.date, narration: v.narration, lines: v.lines.length, amount: r2(v.lines.reduce((a, l) => a + l.debit, 0)), status: v.status, createdBy: v.createdBy })),
  },

  // RECEIVABLES
  {
    id: 'customers', label: 'Customers', tab: 'Receivables', group: 'Parties', icon: Users, color: C.blue,
    columns: [t('code', 'Code', 72), t('name', 'Customer Name', 220), t('trn', 'TRN', 130), t('email', 'Email', 180), t('mobile', 'Mobile', 130), t('group', 'Group', 100), t('ccy', 'Ccy', 46), m('limit', 'Credit Limit'), n('terms', 'Terms (d)', 70), t('sp', 'Salesperson', 90), m('balance', 'Balance')],
    rows: (s) => s.customers.map((c) => ({ code: c.code, name: c.name, trn: c.trn, email: c.email, mobile: c.mobile, group: c.group, ccy: c.currency, limit: c.creditLimit, terms: c.paymentTermsDays, sp: c.salesperson, balance: partyBalance(s, ACC.AR, c.code) })),
  },
  {
    id: 'agents', label: 'Agents', tab: 'Receivables', group: 'Parties', icon: BadgeDollarSign, color: C.gold,
    columns: [t('code', 'Agent Code', 90), t('name', 'Name', 180), t('phone', 'Phone', 140), t('email', 'Email', 200), { key: 'rate', label: 'Commission', width: 90, type: 'pct' }, n('customers', 'Customers', 80), t('status', 'Status', 70)],
    rows: (s) => s.agents.map((a) => ({ code: a.agentCode, name: a.name, phone: a.phone, email: a.email, rate: a.commissionRate, customers: s.customers.filter((c) => c.salesperson === a.agentCode).length, status: a.status })),
  },
  {
    id: 'estimates', label: 'Estimate / Quotation', tab: 'Receivables', group: 'Sales', icon: ClipboardList, color: C.purple,
    columns: [t('no', 'Estimate No', 120), d('date', 'Date'), t('customer', 'Customer', 220), d('valid', 'Valid Until'), t('status', 'Status', 90), m('net', 'Net'), m('vat', 'VAT'), m('total', 'Total')],
    rows: (s) => s.estimates.map((e) => ({ no: e.no, date: e.date, customer: partyName(s, e.party), valid: e.validUntil, status: e.status, ...docTotals(e.lines), total: docTotals(e.lines).gross })),
  },
  {
    id: 'delivery-notes', label: 'Delivery Note', tab: 'Receivables', group: 'Sales', icon: Truck, color: C.orange,
    columns: [t('no', 'Delivery No', 120), d('date', 'Date'), t('customer', 'Customer', 220), t('inv', 'Invoice', 116), t('desc', 'Description', 220), n('qty', 'Qty', 50), t('addr', 'Delivery Address', 200), t('status', 'Status', 80)],
    rows: (s) => s.deliveryNotes.map((x) => ({ no: x.no, date: x.date, customer: partyName(s, x.party), inv: x.againstDoc, desc: x.description, qty: x.qty, addr: x.address, status: x.status })),
  },
  {
    id: 'sales-invoices', label: 'Sales Invoice', tab: 'Receivables', group: 'Sales', icon: FileText, color: C.green, newForm: 'sales-invoice',
    columns: [t('no', 'Invoice No', 116), d('date', 'Date'), t('customer', 'Customer', 220), d('due', 'Due Date'), m('net', 'Net'), m('vat', 'VAT 5%', 90), m('gross', 'Total'), m('outstanding', 'Outstanding'), t('status', 'Status', 100), t('agent', 'Agent', 70)],
    rows: (s) => {
      const rows: Row[] = [...s.salesInvoices].reverse().map((i) => {
        const tt = docTotals(i.lines);
        const st = invoiceStatus(s, i.no, i.dueDate, tt.gross);
        return { no: i.no, date: i.date, customer: partyName(s, i.party), due: i.dueDate, ...tt, outstanding: invoiceOutstanding(s, i.no), status: st, agent: i.agent, ...(st === 'Overdue' ? meta('warn') : {}) };
      });
      return [...rows, totalRow(rows, 'Total', 'customer', ['net', 'vat', 'gross', 'outstanding'], 'grand')];
    },
  },
  {
    id: 'invoice-entry', label: 'Invoice Entry', tab: 'Receivables', group: 'Fast Entry', icon: FileSpreadsheet, color: C.green, kind: 'entry',
    note: 'Type the invoice straight into the cells: customer in the strip, lines in the grid, Ctrl+Enter to post (live mode).',
  },
  {
    id: 'quote-entry', label: 'Quotation Entry', tab: 'Receivables', group: 'Fast Entry', icon: ClipboardList, color: C.purple, kind: 'entry',
    note: 'Type the quotation into the cells; Ctrl+Enter saves it (live mode).',
  },
  {
    id: 'receipt-batch', label: 'Receipt Batch', tab: 'Receivables', group: 'Fast Entry', icon: ListChecks, color: C.green, kind: 'entry',
    note: 'One receipt per row; Post all posts every ready row (live mode).',
  },
  {
    id: 'bill-entry', label: 'Bill Entry', tab: 'Payables', group: 'Fast Entry', icon: FileSpreadsheet, color: C.orange, kind: 'entry',
    note: 'Type the vendor bill into the cells: vendor in the strip, lines in the grid, Ctrl+Enter to post (live mode).',
  },
  {
    id: 'payment-batch', label: 'Payment Batch', tab: 'Payables', group: 'Fast Entry', icon: ListChecks, color: C.orange, kind: 'entry',
    note: 'One vendor payment per row; Post all posts every ready row (live mode).',
  },
  {
    id: 'expense-batch', label: 'Expense Batch', tab: 'Payables', group: 'Fast Entry', icon: Receipt, color: C.red, kind: 'entry',
    note: 'One expense per row; Post all posts every ready row (live mode).',
  },
  {
    id: 'prepay-batch', label: 'New Prepayments', tab: 'Finance', group: 'Prepayments', icon: CalendarRange, color: C.purple, kind: 'entry',
    note: 'One prepayment per row (rent, insurance, licences paid in advance); saved prepayments are released monthly (live mode).',
  },
  {
    id: 'prepay-schedule', label: 'Prepayment Schedule', tab: 'Finance', group: 'Prepayments', icon: CalendarClock, color: C.teal, kind: 'tool',
    note: 'Month-by-month release of every prepayment; Run prepayment release posts what is due (live mode).',
  },
  {
    id: 'asset-batch', label: 'New Assets', tab: 'CRM & Assets', group: 'Fixed Assets', icon: PackagePlus, color: C.teal, kind: 'entry',
    note: 'One asset per row: cost, salvage, life in months, accounts (live mode).',
  },
  {
    id: 'dep-schedule', label: 'Depreciation Schedule', tab: 'CRM & Assets', group: 'Fixed Assets', icon: CalendarClock, color: C.blue, kind: 'tool',
    note: 'Pick an asset to see every month of its depreciation; dispose (sell/scrap) from the strip (live mode).',
  },
  {
    id: 'gratuity', label: 'Gratuity (EOSB)', tab: 'HR & Payroll', group: 'End of Service', icon: HandCoins, color: C.orange, kind: 'tool',
    note: 'UAE end-of-service gratuity per employee, month-end provision and leaver settlement (live mode).',
  },
  {
    id: 'cc-pnl', label: 'Cost Centre P&L', tab: 'Reports', group: 'Management', icon: Grid3x3, color: C.green, kind: 'tool',
    note: 'Income and expenses by cost centre / project, side by side (live mode).',
  },
  { id: 'rpt-monthly-pnl', label: 'Horizontal Profit and Loss', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Horizontal Profit and Loss', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-ratios', label: 'Business Performance Ratios', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Business Performance Ratios', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-equity', label: 'Movement of Equity', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Movement of Equity', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-sales-by-customer', label: 'Sales by Customer', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Sales by Customer', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-sales-by-item', label: 'Sales by Item', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Sales by Item', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-sales-by-salesperson', label: 'Sales by Sales Person', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Sales by Sales Person', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-sales-summary', label: 'Sales Summary', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Sales Summary', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-customer-balances', label: 'Customer Balance Summary', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Customer Balance Summary', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-ar-aging-details', label: 'AR Aging Details', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'AR Aging Details', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-vendor-balances', label: 'Vendor Balance Summary', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Vendor Balance Summary', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-ap-aging-details', label: 'AP Aging Details', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'AP Aging Details', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-purchases-by-vendor', label: 'Purchases by Vendor', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Purchases by Vendor', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-purchases-by-item', label: 'Purchases by Item', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Purchases by Item', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-expenses-by-category', label: 'Expenses by Category', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Expenses by Category', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-vat-return', label: 'VAT Return Summary', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'VAT Return Summary', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-bank-balances', label: 'Bank & Cash Balances', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Bank & Cash Balances', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-bank-book', label: 'Bank Book', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Bank Book', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-account-type-summary', label: 'Account Type Summary', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Account Type Summary', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  { id: 'rpt-expiring-docs', label: 'Expiring Documents', tab: 'Reports', group: 'Reports', icon: FileSpreadsheet, color: C.green, menuOnly: true, columns: [t('x', 'Expiring Documents', 560)], rows: () => [{ x: 'Live report — sign in to a company to run it.', ...meta('muted') }] },
  {
    id: 'recurring-invoices', label: 'Recurring Invoices', tab: 'Receivables', group: 'Sales', icon: Repeat, color: C.teal,
    columns: [t('profile', 'Profile', 90), t('customer', 'Customer', 220), t('desc', 'Description', 220), t('freq', 'Frequency', 90), d('next', 'Next Invoice'), m('amount', 'Amount (net)'), t('active', 'Active', 60)],
    rows: (s) => [
      { profile: 'REC-001', customer: partyName(s, 'C-0001'), desc: 'Bookkeeping (monthly)', freq: 'Monthly', next: '2026-10-01', amount: 6500, active: 'Yes' },
      { profile: 'REC-002', customer: partyName(s, 'C-0002'), desc: 'Payroll Processing / WPS', freq: 'Monthly', next: '2026-10-01', amount: 3500, active: 'Yes' },
      { profile: 'REC-003', customer: partyName(s, 'C-0004'), desc: 'VAT Return Filing', freq: 'Quarterly', next: '2026-10-28', amount: 2500, active: 'Yes' },
    ],
  },
  {
    id: 'receipts', label: 'Receipt Voucher', tab: 'Receivables', group: 'Collections', icon: HandCoins, color: C.green, newForm: 'receipt',
    columns: [t('no', 'Receipt No', 116), d('date', 'Date'), t('customer', 'Customer', 220), t('against', 'Against', 116), t('mode', 'Mode', 110), t('ref', 'Reference', 90), t('bank', 'Deposited To', 200), m('amount', 'Amount')],
    rows: (s) => {
      const rows: Row[] = [...s.receipts].reverse().map((r) => ({ no: r.no, date: r.date, customer: partyName(s, r.party), against: r.againstDoc ?? 'On account', mode: r.paymentMode, ref: r.reference, bank: accountName(s, r.bankAccount), amount: r.amount }));
      return [...rows, totalRow(rows, 'Total', 'customer', ['amount'], 'grand')];
    },
  },
  {
    id: 'credit-notes', label: 'Credit Note', tab: 'Receivables', group: 'Collections', icon: FileMinus, color: C.red,
    columns: [t('no', 'Credit Note No', 116), d('date', 'Date'), t('customer', 'Customer', 220), t('against', 'Against', 116), t('reason', 'Reason', 220), m('net', 'Net'), m('vat', 'VAT'), m('gross', 'Total')],
    rows: (s) => s.creditNotes.map((c) => ({ no: c.no, date: c.date, customer: partyName(s, c.party), against: c.againstDoc, reason: c.reason, ...docTotals(c.lines) })),
  },
  {
    id: 'ar-aging', label: 'AR Aging', tab: 'Receivables', group: 'Analysis', icon: Clock, color: C.orange,
    columns: [t('code', 'Code', 72), t('name', 'Customer', 240), m('current', 'Current'), m('d30', '1–30 days'), m('d60', '31–60 days'), m('d90', '61–90 days'), m('d90p', '90+ days'), m('total', 'Total')],
    rows: (s) => { const rows = aging(s, 'AR', TODAY) as unknown as Row[]; return [...rows, totalRow(rows, 'Total', 'name', ['current', 'd30', 'd60', 'd90', 'd90p', 'total'], 'grand')]; },
  },
  {
    id: 'outstanding', label: 'Outstanding Report', tab: 'Receivables', group: 'Analysis', icon: ListChecks, color: C.orange,
    columns: [t('no', 'Invoice No', 116), d('date', 'Date'), d('due', 'Due Date'), t('customer', 'Customer', 220), m('gross', 'Invoice Total'), m('outstanding', 'Outstanding'), n('late', 'Days Overdue', 90)],
    rows: (s) => {
      const rows: Row[] = s.salesInvoices.filter((i) => invoiceOutstanding(s, i.no) > 0).map((i) => {
        const late = Math.max(0, Math.floor((Date.parse(TODAY) - Date.parse(i.dueDate)) / 86_400_000));
        return { no: i.no, date: i.date, due: i.dueDate, customer: partyName(s, i.party), gross: docTotals(i.lines).gross, outstanding: invoiceOutstanding(s, i.no), late, ...(late > 0 ? meta('warn') : {}) };
      });
      return [...rows, totalRow(rows, 'Total', 'customer', ['gross', 'outstanding'], 'grand')];
    },
  },
  {
    id: 'transactions', label: 'Transactions', tab: 'Receivables', group: 'Analysis', icon: ArrowLeftRight, color: C.blue,
    columns: [d('date', 'Date'), t('no', 'Document No', 120), t('type', 'Type', 110), t('customer', 'Customer', 220), m('debit', 'Debit'), m('credit', 'Credit'), m('balance', 'Customer Bal.')],
    rows: (s) => {
      const names: Record<string, string> = { INV: 'Sales Invoice', RV: 'Receipt', CN: 'Credit Note' };
      const run = new Map<string, number>();
      return [...s.vouchers].sort((a, b) => a.date.localeCompare(b.date)).flatMap((v) => v.lines.filter((l) => l.account === ACC.AR && l.party).map((l) => {
        const bal = r2((run.get(l.party!) ?? 0) + l.debit - l.credit);
        run.set(l.party!, bal);
        return { date: v.date, no: v.voucherNo, type: names[v.type] ?? v.type, customer: partyName(s, l.party), debit: l.debit, credit: l.credit, balance: bal };
      }));
    },
  },

  // PAYABLES
  {
    id: 'vendors', label: 'Vendors', tab: 'Payables', group: 'Parties', icon: Building2, color: C.blue,
    columns: [t('code', 'Code', 72), t('name', 'Vendor Name', 220), t('trn', 'TRN', 130), t('email', 'Email', 200), t('group', 'Group', 110), t('ccy', 'Ccy', 46), n('terms', 'Terms (d)', 70), m('balance', 'Balance Payable', 120)],
    rows: (s) => s.vendors.map((v) => ({ code: v.code, name: v.name, trn: v.trn, email: v.email, group: v.group, ccy: v.currency, terms: v.paymentTermsDays, balance: -partyBalance(s, ACC.AP, v.code) })),
  },
  {
    id: 'purchase-invoices', label: 'Purchase Invoice', tab: 'Payables', group: 'Purchases', icon: FilePlus, color: C.orange, newForm: 'purchase-invoice',
    columns: [t('no', 'Bill No', 116), d('date', 'Date'), t('vendor', 'Vendor', 220), d('due', 'Due Date'), m('net', 'Net'), m('vat', 'Input VAT', 90), m('gross', 'Total'), m('outstanding', 'Outstanding'), t('status', 'Status', 100)],
    rows: (s) => {
      const rows: Row[] = [...s.purchaseInvoices].reverse().map((i) => {
        const tt = docTotals(i.lines);
        const st = billStatus(s, i.no, i.dueDate, tt.gross);
        return { no: i.no, date: i.date, vendor: partyName(s, i.party), due: i.dueDate, ...tt, outstanding: billOutstanding(s, i.no), status: st, ...(st === 'Overdue' ? meta('warn') : {}) };
      });
      return [...rows, totalRow(rows, 'Total', 'vendor', ['net', 'vat', 'gross', 'outstanding'], 'grand')];
    },
  },
  {
    id: 'expenses', label: 'Expenses', tab: 'Payables', group: 'Purchases', icon: Receipt, color: C.red, newForm: 'expense',
    columns: [t('no', 'Expense No', 116), d('date', 'Date'), t('vendor', 'Vendor', 180), t('narration', 'Narration', 240), t('paidFrom', 'Paid From', 190), m('net', 'Net'), m('vat', 'VAT'), m('gross', 'Total')],
    rows: (s) => [...s.expenses].reverse().map((e) => ({ no: e.no, date: e.date, vendor: partyName(s, e.vendor), narration: e.narration, paidFrom: accountName(s, e.bankAccount), ...docTotals(e.lines) })),
  },
  {
    id: 'payments', label: 'Payment Voucher', tab: 'Payables', group: 'Payments', icon: Banknote, color: C.green, newForm: 'payment',
    columns: [t('no', 'Payment No', 116), d('date', 'Date'), t('vendor', 'Vendor', 220), t('against', 'Against', 116), t('mode', 'Mode', 110), t('bank', 'Paid From', 200), m('amount', 'Amount')],
    rows: (s) => [...s.payments].reverse().map((p) => ({ no: p.no, date: p.date, vendor: partyName(s, p.party), against: p.againstDoc ?? 'On account', mode: p.paymentMode, bank: accountName(s, p.bankAccount), amount: p.amount })),
  },
  {
    id: 'debit-notes', label: 'Debit Note', tab: 'Payables', group: 'Payments', icon: FileX, color: C.red,
    columns: [t('no', 'Debit Note No', 116), d('date', 'Date'), t('vendor', 'Vendor', 220), t('against', 'Against', 116), t('reason', 'Reason', 220), m('net', 'Net'), m('vat', 'VAT'), m('gross', 'Total')],
    rows: (s) => s.debitNotes.map((c) => ({ no: c.no, date: c.date, vendor: partyName(s, c.party), against: c.againstDoc, reason: c.reason, ...docTotals(c.lines) })),
  },
  {
    id: 'ap-aging', label: 'AP Aging', tab: 'Payables', group: 'Analysis', icon: Clock, color: C.orange,
    columns: [t('code', 'Code', 72), t('name', 'Vendor', 240), m('current', 'Current'), m('d30', '1–30 days'), m('d60', '31–60 days'), m('d90', '61–90 days'), m('d90p', '90+ days'), m('total', 'Total')],
    rows: (s) => { const rows = aging(s, 'AP', TODAY) as unknown as Row[]; return [...rows, totalRow(rows, 'Total', 'name', ['current', 'd30', 'd60', 'd90', 'd90p', 'total'], 'grand')]; },
  },
  {
    id: 'expense-transactions', label: 'Expense Transactions', tab: 'Payables', group: 'Analysis', icon: Coins, color: C.red,
    columns: [d('date', 'Date'), t('voucherNo', 'Voucher', 116), t('account', 'Account', 76), t('name', 'Expense Account', 220), t('narration', 'Narration', 240), m('amount', 'Amount')],
    rows: (s) => {
      const exp = new Set(s.accounts.filter((a) => a.type === 'Expense').map((a) => a.code));
      const rows: Row[] = s.vouchers.flatMap((v) => v.lines.filter((l) => exp.has(l.account)).map((l) => ({ date: v.date, voucherNo: v.voucherNo, account: l.account, name: accountName(s, l.account), narration: l.narration ?? v.narration, amount: r2(l.debit - l.credit) })));
      return [...rows, totalRow(rows, 'Total', 'narration', ['amount'], 'grand')];
    },
  },

  // ITEMS
  {
    id: 'items', label: 'Items', tab: 'Items', group: 'Catalog', icon: Package, color: C.orange,
    columns: [t('code', 'Code', 80), t('name', 'Item Name', 230), t('kind', 'Kind', 70), t('unit', 'Unit', 60), t('category', 'Category', 110), m('sell', 'Selling Price'), m('cost', 'Cost Price'), t('sales', 'Sales Account', 180), t('tax', 'Tax', 50), t('active', 'Active', 56)],
    rows: (s) => s.items.map((i) => ({ code: i.code, name: i.name, kind: i.kind, unit: i.unit, category: i.category, sell: i.sellingPrice, cost: i.costPrice, sales: `${i.salesAccount} ${accountName(s, i.salesAccount)}`, tax: i.taxCode, active: i.isActive ? 'Yes' : 'No' })),
  },
  {
    id: 'item-kits', label: 'Item Kits', tab: 'Items', group: 'Catalog', icon: Boxes, color: C.purple,
    columns: [t('kit', 'Kit', 90), t('name', 'Kit Name / Component', 260), n('qty', 'Qty', 50), m('price', 'Unit Price'), m('amount', 'Amount')],
    rows: (s) => {
      const comp = [['SRV-001', 1], ['SRV-002', 1], ['SRV-004', 1]] as const;
      const lines: Row[] = comp.map(([c, q]) => { const it = s.items.find((i) => i.code === c)!; return { kit: c, name: it.name, qty: q, price: it.sellingPrice, amount: it.sellingPrice * q, ...meta(undefined, { indent: 1 }) }; });
      return [{ kit: 'KIT-001', name: 'New Company Setup Bundle', ...meta('group') }, ...lines, totalRow(lines, 'Kit price', 'name', ['amount'])];
    },
  },
  {
    id: 'units', label: 'Units', tab: 'Items', group: 'Setup', icon: Ruler, color: C.gray,
    columns: [t('unit', 'Unit of Measure', 160), n('used', 'Used by Items', 110), t('active', 'Active', 60)],
    rows: (s) => [...new Set(s.items.map((i) => i.unit).concat(['Hour', 'Piece']))].map((u) => ({ unit: u, used: s.items.filter((i) => i.unit === u).length, active: 'Yes' })),
  },
  {
    id: 'item-categories', label: 'Item Categories', tab: 'Items', group: 'Setup', icon: Tags, color: C.gray,
    columns: [t('cat', 'Category', 180), n('items', 'Items', 70), t('active', 'Active', 60)],
    rows: (s) => [...new Set(s.items.map((i) => i.category))].map((c) => ({ cat: c, items: s.items.filter((i) => i.category === c).length, active: 'Yes' })),
  },
  {
    id: 'stock', label: 'Stock & Warehouses', tab: 'Items', group: 'Inventory', icon: Layers, color: C.gray, planned: true,
    columns: [t('item', 'Item', 200), t('wh', 'Warehouse', 140), n('qty', 'On Hand', 80), m('value', 'Value')],
    rows: () => [], note: 'Planned — also still open in C-ERP (inventory / stock tracking is its one remaining gap).',
  },

  // CRM & ASSETS
  {
    id: 'leads', label: 'Leads', tab: 'CRM & Assets', group: 'CRM', icon: UserPlus, color: C.purple,
    columns: [t('name', 'Contact', 150), t('company', 'Company', 200), t('mobile', 'Mobile', 130), t('source', 'Source', 90), t('stage', 'Stage', 90), m('value', 'Est. Value'), t('assigned', 'Assigned', 80), d('last', 'Last Activity', 100)],
    rows: (s) => s.leads.map((l) => ({ name: l.name, company: l.companyName, mobile: l.mobile, source: l.source, stage: l.stage, value: l.estimatedValue, assigned: l.assignedTo, last: l.lastActivity })),
  },
  {
    id: 'assets', label: 'Fixed Assets', tab: 'CRM & Assets', group: 'Fixed Assets', icon: Car, color: C.teal,
    columns: [t('code', 'Asset Code', 84), t('name', 'Asset', 230), t('cat', 'Category', 100), d('date', 'Purchased'), m('cost', 'Cost'), m('salvage', 'Salvage', 90), n('life', 'Life (m)', 64), m('monthly', 'Monthly Dep.', 100), m('acc', 'Acc. Dep.', 100), m('nbv', 'Net Book Value', 120), t('status', 'Status', 70)],
    rows: (s) => {
      const rows: Row[] = s.assets.map((a) => { const acc = accumulatedDep(s, a.assetCode); return { code: a.assetCode, name: a.name, cat: a.category, date: a.purchaseDate, cost: a.purchaseCost, salvage: a.salvageValue, life: a.usefulLifeMonths, monthly: monthlyDepreciation(a), acc, nbv: r2(a.purchaseCost - acc), status: a.status }; });
      return [...rows, totalRow(rows, 'Total', 'name', ['cost', 'monthly', 'acc', 'nbv'], 'grand')];
    },
  },

  // HR & PAYROLL
  {
    id: 'employees', label: 'Employees', tab: 'HR & Payroll', group: 'People', icon: IdCard, color: C.blue,
    columns: [t('code', 'Code', 60), t('name', 'Full Name', 170), t('desig', 'Designation', 150), t('cc', 'Cost Ctr', 72), d('join', 'Joined'), m('basic', 'Basic', 90), m('allow', 'Allowances', 96), m('gross', 'Gross', 96), m('eosb', 'Gratuity (EOSB)', 120), d('visa', 'Visa Expiry'), t('status', 'Status', 70)],
    rows: (s) => s.employees.map((e) => {
      const allow = e.housingAllowance + e.transportAllowance + e.otherAllowance;
      const soon = e.visaExpiryDate <= new Date(Date.now() + 60 * 86_400_000).toISOString().slice(0, 10);
      return { code: e.employeeCode, name: e.fullName, desig: e.designation, cc: e.costCenter, join: e.joiningDate, basic: e.basicSalary, allow, gross: e.basicSalary + allow, eosb: gratuity(e.basicSalary, e.joiningDate), visa: e.visaExpiryDate, status: e.status, ...(soon ? meta('warn') : {}) };
    }),
  },
  {
    id: 'payroll', label: 'Payroll Runs', tab: 'HR & Payroll', group: 'Payroll', icon: Wallet, color: C.green,
    columns: [t('no', 'Run No', 116), t('period', 'Period', 90), d('date', 'Run Date'), n('emp', 'Employees', 80), m('basic', 'Basic'), m('allow', 'Allowances'), m('gross', 'Gross'), t('paid', 'Paid (WPS)', 90)],
    rows: (s) => s.payrollRuns.map((r) => ({ no: r.no, period: r.period, date: r.runDate, emp: r.employees.length, ...payrollTotals(s, r), allow: payrollTotals(s, r).allowances, paid: r.isPaid ? 'Paid' : 'Unpaid' })),
  },
  {
    id: 'wps', label: 'WPS / Payslips', tab: 'HR & Payroll', group: 'Payroll', icon: FileSpreadsheet, color: C.teal,
    columns: [t('code', 'Employee', 70), t('name', 'Name', 170), t('iban', 'IBAN', 230), m('basic', 'Basic', 90), m('housing', 'Housing', 90), m('transport', 'Transport', 90), m('other', 'Other', 80), m('net', 'Net Pay', 100)],
    rows: (s) => {
      const rows: Row[] = s.employees.filter((e) => e.status === 'Active').map((e) => ({ code: e.employeeCode, name: e.fullName, iban: e.iban, basic: e.basicSalary, housing: e.housingAllowance, transport: e.transportAllowance, other: e.otherAllowance, net: e.basicSalary + e.housingAllowance + e.transportAllowance + e.otherAllowance }));
      return [...rows, totalRow(rows, 'Total (SIF)', 'name', ['basic', 'housing', 'transport', 'other', 'net'], 'grand')];
    },
  },
  {
    id: 'leave-requests', label: 'Leave Requests', tab: 'HR & Payroll', group: 'Self-Service', icon: CalendarDays, color: C.teal,
    columns: [t('employee', 'Employee', 180), t('type', 'Type', 80), d('from', 'From'), d('to', 'To'), t('status', 'Status', 90)],
    rows: () => [], note: 'Employees request leave in the portal; approve or reject the selected row from the ribbon (live mode).',
  },
  {
    id: 'ess', label: 'Employee Portal', tab: 'HR & Payroll', group: 'Self-Service', icon: CircleUser, color: C.purple,
    columns: [t('feature', 'Employee Self-Service', 260), t('status', 'How', 420)],
    rows: () => [
      { feature: 'Profile', status: 'Employee signs in with their own login (grant it from the Employees sheet → Portal Access)' },
      { feature: 'Payslips', status: 'Every posted payroll run, with net pay and payment status' },
      { feature: 'Leave requests', status: 'Request annual / sick / unpaid leave; see balance and decisions' },
      { feature: 'Salary advances', status: 'Outstanding balance and monthly recovery' },
    ],
    note: 'Employee logins open the self-service portal instead of the workbook.',
  },

  // REPORTS
  {
    id: 'trial-balance', label: 'Trial Balance', tab: 'Reports', group: 'Statements', icon: Scale, color: C.blue,
    columns: [t('code', 'Account', 80), t('name', 'Account Name', 280), t('type', 'Type', 90), m('debit', 'Debit', 130), m('credit', 'Credit', 130)],
    rows: (s) => { const rows = trialBalance(s, TODAY) as unknown as Row[]; return [...rows, totalRow(rows, 'Total — in balance ✓', 'name', ['debit', 'credit'], 'grand')]; },
  },
  {
    id: 'pnl', label: 'Profit & Loss', tab: 'Reports', group: 'Statements', icon: TrendingUp, color: C.green,
    columns: [t('code', 'Account', 80), t('name', 'Particulars', 300), t('cat', 'Group', 200), m('amount', 'Amount (AED)', 140)],
    rows: (s) => {
      const p = pnl(s, FY_START, TODAY);
      const sec = (xs: typeof p.revenue) => xs.map((r) => ({ code: r.code, name: r.name, cat: r.category, amount: r.amount, ...meta(undefined, { indent: 1 }) }));
      return [
        { name: `Income  (${FY_START} → ${TODAY})`, ...meta('group') }, ...sec(p.revenue), { name: 'Total Income', amount: p.totalRev, ...meta('total') },
        { name: 'Expenses', ...meta('group') }, ...sec(p.expenses), { name: 'Total Expenses', amount: p.totalExp, ...meta('total') },
        { name: p.net >= 0 ? 'Net Profit' : 'Net Loss', amount: p.net, ...meta('grand') },
      ];
    },
  },
  {
    id: 'balance-sheet', label: 'Balance Sheet', tab: 'Reports', group: 'Statements', icon: ChartPie, color: C.purple,
    columns: [t('code', 'Account', 80), t('name', 'Particulars', 300), t('cat', 'Classification', 200), m('amount', 'Amount (AED)', 140)],
    rows: (s) => {
      const b = balanceSheet(s, TODAY);
      const sec = (xs: typeof b.assets) => xs.map((r) => ({ code: r.code, name: r.name, cat: r.category, amount: r.amount, ...meta(undefined, { indent: 1 }) }));
      return [
        { name: `Assets  (as of ${TODAY})`, ...meta('group') }, ...sec(b.assets), { name: 'Total Assets', amount: b.totalAssets, ...meta('grand') },
        { name: 'Liabilities', ...meta('group') }, ...sec(b.liabilities), { name: 'Total Liabilities', amount: b.totalLiab, ...meta('total') },
        { name: 'Equity', ...meta('group') }, ...sec(b.equity), { name: 'Current year profit', amount: b.profit, ...meta(undefined, { indent: 1 }) }, { name: 'Total Equity', amount: b.totalEquity, ...meta('total') },
        { name: 'Total Liabilities & Equity', amount: r2(b.totalLiab + b.totalEquity), ...meta('grand') },
      ];
    },
  },
  {
    id: 'cash-flow', label: 'Cash Flow', tab: 'Reports', group: 'Statements', icon: Waves, color: C.teal,
    columns: [t('name', 'Particulars', 340), m('amount', 'Amount (AED)', 140)],
    rows: (s) => {
      const cf = cashFlow(s, FY_START, TODAY);
      const out: Row[] = [{ name: 'Opening cash & bank', amount: cf.opening, ...meta('total') }];
      let net = 0;
      for (const [bucket, map] of Object.entries(cf.buckets)) {
        out.push({ name: `${bucket} activities`, ...meta('group') });
        let sub = 0;
        for (const [k, v] of map) { out.push({ name: k, amount: v, ...meta(undefined, { indent: 1 }) }); sub += v; }
        out.push({ name: `Net cash from ${bucket.toLowerCase()}`, amount: r2(sub), ...meta('total') });
        net += sub;
      }
      out.push({ name: 'Closing cash & bank', amount: r2(cf.opening + net), ...meta('grand') });
      return out;
    },
  },
  {
    id: 'mis', label: 'MIS & Segments', tab: 'Reports', group: 'Management', icon: ChartColumn, color: C.orange,
    columns: [t('segment', 'Segment', 260), m('revenue', 'Revenue'), m('share', 'Share %', 90)],
    rows: (s) => {
      const byCc = new Map<string, number>();
      const byGroup = new Map<string, number>();
      for (const i of s.salesInvoices) for (const l of i.lines) {
        const net = r2(l.quantity * l.unitPrice);
        const cc = s.costCenters.find((c) => c.code === l.costCenter)?.name ?? 'Unassigned';
        byCc.set(cc, r2((byCc.get(cc) ?? 0) + net));
        const g = s.customers.find((c) => c.code === i.party)?.group ?? 'Other';
        byGroup.set(g, r2((byGroup.get(g) ?? 0) + net));
      }
      const total = [...byCc.values()].reduce((a, b) => a + b, 0) || 1;
      const block = (title: string, mp: Map<string, number>): Row[] => [{ segment: title, ...meta('group') }, ...[...mp].sort((a, b) => b[1] - a[1]).map(([k, v]) => ({ segment: k, revenue: v, share: `${((v / total) * 100).toFixed(1)}%`, ...meta(undefined, { indent: 1 }) }))];
      return [...block('By cost center', byCc), ...block('By customer group', byGroup), { segment: 'Total revenue', revenue: r2(total), share: '100%', ...meta('grand') }];
    },
  },
  {
    id: 'commission-report', label: 'Commission Report', tab: 'Reports', group: 'Management', icon: Percent, color: C.gold,
    columns: [t('agent', 'Agent', 150), t('no', 'Invoice', 116), t('customer', 'Customer', 220), m('net', 'Net Sales'), { key: 'rate', label: 'Rate', width: 60, type: 'pct' }, m('commission', 'Commission')],
    rows: (s) => {
      const rows: Row[] = s.salesInvoices.filter((i) => i.agent).map((i) => { const a = s.agents.find((x) => x.agentCode === i.agent)!; const net = docTotals(i.lines).net; return { agent: a.name, no: i.no, customer: partyName(s, i.party), net, rate: a.commissionRate, commission: r2(net * a.commissionRate) }; });
      return [...rows, totalRow(rows, 'Total', 'customer', ['net', 'commission'], 'grand')];
    },
  },
  {
    id: 'audit-log', label: 'Audit Log', tab: 'Reports', group: 'Audit', icon: History, color: C.gray,
    columns: [t('at', 'When', 130), t('user', 'User', 200), t('action', 'Action', 110), t('entity', 'Entity', 130), t('detail', 'Detail', 380)],
    rows: (s) => s.audit.map((a) => ({ at: a.at, user: a.user, action: a.action, entity: a.entity, detail: a.detail })),
  },

  // SETTINGS
  {
    id: 'company-setup', label: 'Company Setup', tab: 'Settings', group: 'Company', icon: Building, color: C.blue,
    columns: [t('field', 'Setting', 200), t('value', 'Value', 380)],
    rows: (s) => [
      { field: 'Legal name', value: s.company.name }, { field: 'TRN (VAT)', value: s.company.trn }, { field: 'Address', value: s.company.address },
      { field: 'Base currency', value: s.company.baseCurrency }, { field: 'Fiscal year', value: 'Jan – Dec' }, { field: 'PRO-Service mode', value: s.company.proServiceMode ? 'On (Service Kits enabled)' : 'Off' },
      { field: 'Invoice numbering', value: 'INV-{yyyy}-{0000}, shared with GL voucher' },
    ],
  },
  {
    id: 'companies', label: 'Companies & Access', tab: 'Settings', group: 'Company', icon: Network, color: C.purple,
    columns: [t('company', 'Company', 240), t('trn', 'TRN', 140), n('users', 'Users', 60), t('status', 'Status', 80)],
    rows: (s) => [
      { company: s.company.name, trn: s.company.trn, users: 3, status: 'Active' },
      { company: 'Client B — Sample Trading LLC', trn: '100111222333003', users: 2, status: 'Active' },
    ],
  },
  {
    id: 'users', label: 'Manage Team', tab: 'Settings', group: 'Company', icon: UserCog, color: C.purple,
    columns: [t('email', 'User', 230), t('role', 'Role', 120), t('company', 'Company Access', 220), t('payroll', 'Payroll Access', 110)],
    rows: (s) => [
      { email: 'owner@asco.local', role: 'FirmAdmin', company: 'All companies', payroll: 'Yes' },
      { email: 'accounts@asco.local', role: 'Accountant', company: s.company.name, payroll: 'No' },
      { email: 'readonly@asco.local', role: 'Viewer', company: s.company.name, payroll: 'No' },
    ],
  },
  {
    id: 'fiscal-periods', label: 'Fiscal Periods', tab: 'Settings', group: 'Accounting', icon: CalendarDays, color: C.teal,
    columns: [t('name', 'Period', 100), n('no', 'No', 44), d('start', 'Start'), d('end', 'End'), t('status', 'Status', 80)],
    rows: (s) => s.periods.map((p) => ({ name: p.name, no: p.periodNo, start: p.startDate, end: p.endDate, status: p.isClosed ? 'Closed' : 'Open', ...(p.isClosed ? meta('muted') : {}) })),
  },
  {
    id: 'cost-centers', label: 'Cost Centers', tab: 'Settings', group: 'Accounting', icon: Split, color: C.teal,
    columns: [t('code', 'Code', 90), t('name', 'Cost Center', 240), t('active', 'Active', 60)],
    rows: (s) => s.costCenters.map((c) => ({ code: c.code, name: c.name, active: c.isActive ? 'Yes' : 'No' })),
  },
  {
    id: 'currencies', label: 'Currencies', tab: 'Settings', group: 'Accounting', icon: Landmark, color: C.gold,
    columns: [t('code', 'Code', 60), t('name', 'Currency', 200), { key: 'rate', label: 'Rate to AED', width: 100, type: 'number' }, t('active', 'Active', 60)],
    rows: (s) => s.currencies.map((c) => ({ code: c.code, name: c.name, rate: c.rateToBase, active: c.isActive ? 'Yes' : 'No' })),
  },
  {
    id: 'tax-configuration', label: 'Tax Configuration', tab: 'Settings', group: 'Accounting', icon: Calculator, color: C.red,
    columns: [t('code', 'Tax Code', 70), t('desc', 'Description', 220), { key: 'rate', label: 'Rate', width: 60, type: 'pct' }, t('type', 'Type', 110), t('out', 'Output Account', 170), t('in', 'Input Account', 170)],
    rows: (s) => s.taxCodes.map((x) => ({ code: x.code, desc: x.description, rate: x.rate, type: x.taxType, out: `${x.outputAccount} ${accountName(s, x.outputAccount)}`, in: `${x.inputAccount} ${accountName(s, x.inputAccount)}` })),
  },
  {
    id: 'commission-config', label: 'Commission Config', tab: 'Settings', group: 'Sales Setup', icon: Percent, color: C.gold,
    columns: [t('agent', 'Agent', 160), { key: 'rate', label: 'Base Rate', width: 80, type: 'pct' }, t('basis', 'Basis', 200), t('slab', 'Profit Slabs', 220)],
    rows: (s) => s.agents.map((a) => ({ agent: a.name, rate: a.commissionRate, basis: 'Net invoice value (excl. VAT)', slab: '> 50k/quarter → +1%' })),
  },
  {
    id: 'service-kits', label: 'Service Kits', tab: 'Settings', group: 'Sales Setup', icon: Wand2, color: C.purple,
    columns: [t('kit', 'Kit / Line', 260), m('govt', 'Govt Fee', 90), m('center', 'Center Fee', 90), m('bank', 'Bank Charge', 90), { key: 'vat', label: 'VAT', width: 50, type: 'pct' }],
    rows: () => [
      { kit: 'Trade Licence Renewal', ...meta('group') },
      { kit: 'DED renewal fee', govt: 1200, center: 150, bank: 10, vat: 0, ...meta(undefined, { indent: 1 }) },
      { kit: 'Typing & service charge', govt: 0, center: 250, bank: 0, vat: 0.05, ...meta(undefined, { indent: 1 }) },
      { kit: 'Employment Visa', ...meta('group') },
      { kit: 'Entry permit', govt: 1150, center: 70, bank: 3, vat: 0, ...meta(undefined, { indent: 1 }) },
      { kit: 'Medical & Emirates ID', govt: 690, center: 70, bank: 3, vat: 0, ...meta(undefined, { indent: 1 }) },
    ],
    note: 'Only shown when the company is in PRO-Service mode (same rule as C-ERP).',
  },
  {
    id: 'custom-fields', label: 'Custom Fields & Tags', tab: 'Settings', group: 'Sales Setup', icon: Tags, color: C.gray,
    columns: [t('entity', 'Entity', 140), t('field', 'Field', 180), t('type', 'Type', 90), t('req', 'Required', 70)],
    rows: () => [
      { entity: 'Customer', field: 'Emirate', type: 'Dropdown', req: 'No' }, { entity: 'Customer', field: 'Licence expiry', type: 'Date', req: 'No' },
      { entity: 'Sales Invoice', field: 'Project code', type: 'Text', req: 'No' }, { entity: 'Tags', field: 'VIP, Retainer, Free-zone', type: 'Tag set', req: '—' },
    ],
  },

  // AI
  {
    id: 'ask-ai', label: 'Ask AI', tab: 'AI', group: 'Assistant', icon: Sparkles, color: C.purple, kind: 'ai',
    note: 'Natural-language questions over your books — Phase 4 (Claude API + tool-use over the read models).',
  },
  {
    id: 'insights', label: 'Insights', tab: 'AI', group: 'Assistant', icon: TriangleAlert, color: C.orange,
    columns: [t('severity', 'Severity', 80), t('area', 'Area', 120), t('finding', 'Finding', 460), t('action', 'Suggested Action', 240)],
    rows: (s) => {
      const out: Row[] = [];
      for (const i of s.salesInvoices) {
        const o = invoiceOutstanding(s, i.no);
        const late = Math.floor((Date.parse(TODAY) - Date.parse(i.dueDate)) / 86_400_000);
        if (o > 0 && late > 30) out.push({ severity: late > 90 ? 'High' : 'Medium', area: 'Receivables', finding: `${i.no} — ${partyName(s, i.party)} owes ${o.toFixed(2)} AED, ${late} days overdue`, action: 'Send reminder / statement', ...meta('warn') });
      }
      for (const c of s.customers) {
        const bal = partyBalance(s, ACC.AR, c.code);
        if (c.creditLimit && bal > c.creditLimit) out.push({ severity: 'Medium', area: 'Credit control', finding: `${c.name} balance ${bal.toFixed(2)} exceeds credit limit ${c.creditLimit}`, action: 'Hold new invoices' });
      }
      for (const e of s.employees) {
        const days = Math.floor((Date.parse(e.visaExpiryDate) - Date.parse(TODAY)) / 86_400_000);
        if (days <= 60) out.push({ severity: days < 30 ? 'High' : 'Low', area: 'HR compliance', finding: `${e.fullName} visa expires ${e.visaExpiryDate} (${days} days)`, action: 'Start renewal with PRO' });
      }
      const unpaid = s.payrollRuns.filter((r) => !r.isPaid);
      if (unpaid.length) out.push({ severity: 'Medium', area: 'Payroll', finding: `${unpaid.length} payroll run(s) posted but not paid via WPS`, action: 'Mark paid after bank transfer' });
      if (!out.length) out.push({ severity: 'OK', area: '—', finding: 'No issues detected', action: '' });
      return out;
    },
    note: 'Rule-based today; the same sheet becomes the AI anomaly feed in Phase 4.',
  },
  {
    id: 'bill-scan', label: 'Scan Bill', tab: 'AI', group: 'Automation', icon: ScanLine, color: C.teal, kind: 'scan',
    columns: [t('step', 'Pipeline step', 240), t('detail', 'Detail', 460)],
    rows: () => [
      { step: '1. Upload PDF / photo', detail: 'Drag a vendor bill onto the sheet' },
      { step: '2. Extract', detail: 'Claude vision → vendor, TRN, date, lines, VAT (C-ERP already has BillScanningService)' },
      { step: '3. Match', detail: 'Vendor + expense account suggestion from history' },
      { step: '4. Review & post', detail: 'Pre-filled Purchase Invoice → human approves → posted via engine' },
    ],
    note: 'Live mode: upload a bill; C-ERP\'s BillScanningService extracts it into a purchase-invoice draft.',
  },
  {
    id: 'ai-guardrails', label: 'AI Audit Trail', tab: 'AI', group: 'Automation', icon: ShieldCheck, color: C.gray,
    columns: [t('rule', 'Guardrail', 300), t('why', 'Why', 400)],
    rows: () => [
      { rule: 'AI never posts directly', why: 'It drafts; a user with CanPost approves → same posting engine' },
      { rule: 'Read-only tools by default', why: 'Queries run on read models scoped to the active company' },
      { rule: 'Every AI answer logged', why: 'Mirrors C-ERP QueryLog entity for review/eval' },
    ],
  },

  // ── INDUSTRY MODULES (ASCO-owned; enabled per company by industry profile) ──
  {
    id: 'industry-modules', label: 'Industry & Modules', tab: 'Settings', group: 'Company', icon: SlidersHorizontal, color: C.teal,
    columns: [t('industry', 'Industry', 220), t('modules', 'Default modules', 260), t('description', 'What it adds', 460)],
    rows: () => [
      { industry: 'General / Services firm', modules: '—', description: 'Core accounting, AR/AP, payroll, reports' },
      { industry: 'Trading & Distribution', modules: 'Inventory', description: 'Warehouses, weighted-average cost, COGS on invoicing, reorder alerts' },
      { industry: 'Retail', modules: 'Inventory', description: 'Store stock, sell-through to COGS, counts & adjustments' },
      { industry: 'Manufacturing', modules: 'Inventory + Manufacturing', description: 'BOMs, production orders, finished-goods costing' },
      { industry: 'Logistics & Freight', modules: 'Jobs + Fleet', description: 'Shipment job files (AWB/BL, containers), per-job P&L, trucks & trips' },
      { industry: 'Construction & Projects', modules: 'Jobs + Inventory', description: 'Project budgets vs actual cost, site materials' },
      { industry: 'Professional & PRO services', modules: 'Jobs', description: 'Engagement profitability' },
    ],
    note: 'Pick the company\'s industry and map its GL accounts with Configure Industry (live mode, admins).',
  },
  { id: 'stock-on-hand', label: 'Stock on Hand', tab: 'Inventory', group: 'Stock', icon: Blocks, color: C.orange, module: 'inventory', columns: [t('x', 'Stock on Hand', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'stock-moves', label: 'Stock Movements', tab: 'Inventory', group: 'Stock', icon: ArrowLeftRight, color: C.blue, module: 'inventory', columns: [t('x', 'Stock Movements', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'stock-valuation', label: 'Valuation', tab: 'Inventory', group: 'Reports', icon: Scale, color: C.green, module: 'inventory', columns: [t('x', 'Valuation', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'reorder', label: 'Reorder Alerts', tab: 'Inventory', group: 'Reports', icon: TriangleAlert, color: C.red, module: 'inventory', columns: [t('x', 'Reorder', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'warehouses', label: 'Warehouses', tab: 'Inventory', group: 'Setup', icon: Warehouse, color: C.gray, module: 'inventory', columns: [t('x', 'Warehouses', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'boms', label: 'Bills of Materials', tab: 'Manufacturing', group: 'Engineering', icon: Layers, color: C.purple, module: 'manufacturing', columns: [t('x', 'BOMs', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'production-orders', label: 'Production Orders', tab: 'Manufacturing', group: 'Shop Floor', icon: Factory, color: C.orange, module: 'manufacturing', columns: [t('x', 'Production Orders', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'requirements', label: 'Material Requirements', tab: 'Manufacturing', group: 'Planning', icon: ClipboardCheck, color: C.teal, module: 'manufacturing', columns: [t('x', 'Requirements', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'jobs', label: 'Jobs', tab: 'Jobs', group: 'Jobs', icon: Briefcase, color: C.blue, module: 'jobs', columns: [t('x', 'Jobs', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'shipments', label: 'Shipment Tracker', tab: 'Jobs', group: 'Jobs', icon: Ship, color: C.teal, module: 'jobs', columns: [t('x', 'Shipments', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'vehicles', label: 'Vehicles', tab: 'Jobs', group: 'Fleet', icon: Truck, color: C.orange, module: 'fleet', columns: [t('x', 'Vehicles', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'trips', label: 'Trips', tab: 'Jobs', group: 'Fleet', icon: Route, color: C.green, module: 'fleet', columns: [t('x', 'Trips', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
  { id: 'fuel', label: 'Fleet Costs', tab: 'Jobs', group: 'Fleet', icon: Fuel, color: C.red, module: 'fleet', columns: [t('x', 'Fleet costs', 500)], rows: () => [], note: 'Industry module — runs on the live API. Sign in, then enable it under Settings → Industry & Modules.' },
];

/** Base tab order; module tabs (Inventory / Manufacturing / Jobs) are filtered per company in the app. */
export const TABS = ['Home', 'Finance', 'Receivables', 'Payables', 'Items', 'Inventory', 'Manufacturing', 'Jobs', 'CRM & Assets', 'HR & Payroll', 'Reports', 'Settings', 'AI'];
export const MODULE_TABS: Record<string, 'inventory' | 'manufacturing' | 'jobs'> = { Inventory: 'inventory', Manufacturing: 'manufacturing', Jobs: 'jobs' };

export const screenById = (id: string) => SCREENS.find((x) => x.id === id)!;
