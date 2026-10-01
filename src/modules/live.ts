// Live mode: which ASCO API endpoint feeds each sheet, and how its JSON becomes grid rows.
// Field names are the camelCased C-ERP read models / DTOs from server/Asco.Api/ReadEndpoints.cs.
import type { Col, Row, RowMeta } from './registry';
import { REPORT_LIVE } from './reportsLive';
import { REPORT_DATES, type DateMode, type DateState } from './reportDates';

type J = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
export interface LiveSpec { path: string; columns: Col[]; rows?: (data: any, dates: DateState) => Row[]; payroll?: boolean; admin?: boolean; dates?: DateMode } // eslint-disable-line @typescript-eslint/no-explicit-any

const t = (key: string, label: string, width = 120): Col => ({ key, label, width });
const m = (key: string, label: string, width = 110): Col => ({ key, label, width, type: 'money' });
const d = (key: string, label: string, width = 96): Col => ({ key, label, width, type: 'date' });
const n = (key: string, label: string, width = 70): Col => ({ key, label, width, type: 'number' });
const pct = (key: string, label: string, width = 70): Col => ({ key, label, width, type: 'pct' });

const meta = (style?: RowMeta['style'], indent?: number, formula?: boolean) => ({ _meta: { style, indent, formula: formula ? {} : undefined } as RowMeta });
const sum = (rows: J[], k: string) => Math.round(rows.reduce((a, r) => a + (Number(r[k]) || 0), 0) * 100) / 100;
function total(rows: J[], labelKey: string, keys: string[], label = 'Total'): Row {
  const r: Row = { [labelKey]: label, ...meta('grand', undefined, true) };
  for (const k of keys) r[k] = sum(rows, k);
  return r;
}
const withTotal = (labelKey: string, keys: string[]) => (data: J[]) => [...data, total(data, labelKey, keys)] as Row[];
const dt = (s?: string | null) => (s ? s.slice(0, 16).replace('T', ' ') : '');

export const LIVE: Record<string, LiveSpec> = {
  dashboard: {
    path: '/dashboard',
    columns: [t('metric', 'Metric', 260), m('value', 'Value', 150), t('note', 'Note', 360)],
    rows: (x: J) => [
      { metric: `KEY FIGURES — ${x.kpis.period}`, ...meta('group') },
      { metric: 'Income (period)', value: x.kpis.income },
      { metric: 'Expense (period)', value: x.kpis.expense },
      { metric: 'Net profit (period)', value: x.kpis.netProfit, ...meta('total') },
      { metric: 'Cash & bank', value: x.kpis.cashAndBank },
      { metric: 'Receivables', value: x.kpis.receivables },
      { metric: 'Payables', value: x.kpis.payables },
      { metric: 'Draft vouchers awaiting posting', value: String(x.kpis.draftVouchers), ...(x.kpis.draftVouchers ? meta('warn') : {}) },
      { metric: 'CASH & BANK ACCOUNTS', ...meta('group') },
      ...x.cash.map((c: J) => ({ metric: `${c.code}  ${c.name}`, value: c.balance, ...meta(undefined, 1) })),
    ],
  },

  'chart-of-accounts': {
    path: '/accounts',
    columns: [t('code', 'Account No', 90), t('name', 'Account Name', 260), t('type', 'Type', 80), t('postable', 'Postable', 76), t('parentCode', 'Parent', 70), t('category', 'Category', 150), t('pnlSection', 'P&L Section', 150), t('currency', 'Ccy', 50), m('balance', 'Balance Dr/(Cr)', 130)],
    rows: (xs: J[]) => xs.map((a) => ({ ...a, postable: a.isPostable ? 'Posting' : 'Header', balance: a.isPostable ? a.balance : undefined, ...meta(a.isPostable ? undefined : a.parentCode ? 'total' : 'group', a.isPostable ? 2 : a.parentCode ? 1 : 0) })),
  },
  'opening-balances': {
    path: '/opening-balances',
    columns: [t('code', 'Account', 90), t('name', 'Account Name', 260), t('type', 'Type', 90), m('debit', 'Debit'), m('credit', 'Credit')],
    rows: withTotal('name', ['debit', 'credit']),
  },
  'general-ledger': {
    path: '/general-ledger',
    columns: [d('date', 'Date'), t('voucherNo', 'Voucher No', 120), t('type', 'Type', 70), t('accountCode', 'Account', 76), t('accountName', 'Account Name', 200), t('narration', 'Narration', 240), t('costCenter', 'Cost Ctr', 80), m('debit', 'Debit'), m('credit', 'Credit'), m('runningBalance', 'Running Bal.')],
    rows: (x: J) => [...x.rows, { narration: `Totals (${x.period})`, debit: x.totalDebit, credit: x.totalCredit, runningBalance: x.closing, ...meta('grand') }],
  },
  'voucher-register': {
    path: '/vouchers',
    columns: [t('voucherNo', 'Voucher No', 120), t('type', 'Type', 90), d('date', 'Date'), t('narration', 'Narration', 300), n('lines', 'Lines', 56), m('amount', 'Amount'), t('status', 'Status', 70), t('approvalStatus', 'Approval', 90), t('createdBy', 'Created By', 170)],
  },

  customers: {
    path: '/customers',
    columns: [t('code', 'Code', 80), t('name', 'Customer Name', 220), t('trn', 'TRN', 130), t('email', 'Email', 180), t('mobile', 'Mobile', 120), t('group', 'Group', 100), t('currency', 'Ccy', 46), m('creditLimit', 'Credit Limit'), n('paymentTermsDays', 'Terms (d)'), t('salesperson', 'Salesperson', 110), m('invoiced', 'Invoiced'), m('received', 'Received'), m('outstanding', 'Outstanding')],
    rows: withTotal('name', ['invoiced', 'received', 'outstanding']),
  },
  agents: {
    path: '/agents',
    columns: [t('agentCode', 'Agent Code', 90), t('name', 'Name', 180), t('phone', 'Phone', 140), t('email', 'Email', 200), t('status', 'Status', 80)],
  },
  estimates: {
    path: '/estimates',
    columns: [t('estimateNo', 'Estimate No', 120), d('date', 'Date'), t('customerName', 'Customer', 220), d('validUntil', 'Valid Until'), t('status', 'Status', 90), m('net', 'Net'), m('vat', 'VAT'), m('gross', 'Total')],
  },
  'delivery-notes': {
    path: '/delivery-notes',
    columns: [t('deliveryNoteNo', 'Delivery No', 120), d('date', 'Date'), t('customerName', 'Customer', 220), t('invoiceNo', 'Invoice', 116), t('deliveryAddress', 'Delivery Address', 220), n('quantity', 'Qty', 60), t('status', 'Status', 80)],
  },
  'sales-invoices': {
    path: '/sales-invoices',
    columns: [t('invoiceNo', 'Invoice No', 120), d('date', 'Date'), t('customerName', 'Customer', 220), d('dueDate', 'Due Date'), m('net', 'Net'), m('vat', 'VAT', 90), m('gross', 'Total'), m('balance', 'Outstanding'), t('status', 'Status', 100), t('salesperson', 'Salesperson', 110)],
    rows: (xs: J[]) => [...xs.map((x) => ({ ...x, ...(x.status === 'Overdue' ? meta('warn') : {}) })), total(xs, 'customerName', ['net', 'vat', 'gross', 'balance'])],
  },
  'recurring-invoices': {
    path: '/recurring-invoices',
    columns: [n('id', 'Profile', 60), t('customerName', 'Customer', 220), t('frequency', 'Frequency', 90), n('repeatEvery', 'Every', 56), d('nextGenerationDate', 'Next Invoice', 100), m('net', 'Amount (net)'), t('active', 'Active', 60), t('narration', 'Narration', 220)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, active: x.isActive ? 'Yes' : 'No' })),
  },
  receipts: {
    path: '/receipts',
    columns: [t('receiptNo', 'Receipt No', 120), d('date', 'Date'), t('customerName', 'Customer', 220), t('invoiceNo', 'Against', 116), t('paymentMode', 'Mode', 110), t('referenceNo', 'Reference', 100), t('bankAccount', 'Deposited To', 180), m('amount', 'Amount'), t('status', 'Status', 70)],
    rows: withTotal('customerName', ['amount']),
  },
  'credit-notes': {
    path: '/credit-notes',
    columns: [t('creditNoteNo', 'Credit Note No', 120), d('date', 'Date'), t('customerName', 'Customer', 220), t('invoiceNo', 'Against', 116), t('reason', 'Reason', 200), t('settlementMethod', 'Settlement', 100), m('net', 'Net'), m('vat', 'VAT'), m('gross', 'Total')],
  },
  'ar-aging': {
    path: '/ar-aging',
    columns: [t('code', 'Code', 80), t('name', 'Customer', 240), m('current', 'Current'), m('days1To30', '1–30 days'), m('days31To60', '31–60 days'), m('days61To90', '61–90 days'), m('over90', '90+ days'), m('unallocatedCredits', 'Unallocated Cr.'), m('total', 'Total')],
    rows: withTotal('name', ['current', 'days1To30', 'days31To60', 'days61To90', 'over90', 'unallocatedCredits', 'total']),
  },
  outstanding: {
    path: '/outstanding-invoices',
    columns: [t('invoiceNo', 'Invoice No', 120), d('date', 'Date'), d('dueDate', 'Due Date'), t('customerName', 'Customer', 220), m('amountDue', 'Amount Due'), n('daysOverdue', 'Days Overdue', 90), t('employeeName', 'Salesperson', 130), t('agentName', 'Agent', 120)],
    rows: (xs: J[]) => [...xs.map((x) => ({ ...x, ...(x.daysOverdue > 0 ? meta('warn') : {}) })), total(xs, 'customerName', ['amountDue'])],
  },
  transactions: {
    path: '/transactions',
    columns: [t('tranRef', 'Tran Ref', 100), t('invoiceNo', 'Invoice', 116), d('invoiceDate', 'Date'), t('customerName', 'Customer', 200), t('description', 'Description', 220), n('quantity', 'Qty', 50), m('amount', 'Amount'), m('govtCost', 'Govt Cost'), m('centerFee', 'Center Fee'), m('vat', 'VAT'), t('done', 'Completed', 80), t('supplierName', 'Supplier', 150)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, done: x.isCompleted ? 'Yes' : 'No' })),
  },

  vendors: {
    path: '/vendors',
    columns: [t('code', 'Code', 80), t('name', 'Vendor Name', 220), t('trn', 'TRN', 130), t('email', 'Email', 190), t('group', 'Group', 110), t('currency', 'Ccy', 46), n('paymentTermsDays', 'Terms (d)'), m('billed', 'Billed'), m('paid', 'Paid'), m('outstanding', 'Outstanding')],
    rows: withTotal('name', ['billed', 'paid', 'outstanding']),
  },
  'purchase-invoices': {
    path: '/purchase-invoices',
    columns: [t('invoiceNo', 'Bill No', 120), d('date', 'Date'), t('vendorName', 'Vendor', 220), d('dueDate', 'Due Date'), m('gross', 'Total'), m('balance', 'Outstanding'), t('status', 'Status', 100)],
    rows: (xs: J[]) => [...xs.map((x) => ({ ...x, ...(x.status === 'Overdue' ? meta('warn') : {}) })), total(xs, 'vendorName', ['gross', 'balance'])],
  },
  expenses: {
    path: '/expenses',
    columns: [t('expenseNo', 'Expense No', 120), d('date', 'Date'), t('vendorName', 'Vendor', 180), t('narration', 'Narration', 240), t('paidFrom', 'Paid From', 180), t('payLater', 'Pay Later', 70), m('net', 'Net'), m('vat', 'VAT'), m('gross', 'Total')],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, payLater: x.isPayLater ? 'Yes' : '' })),
  },
  payments: {
    path: '/vendor-payments',
    columns: [t('paymentNo', 'Payment No', 120), d('date', 'Date'), t('vendorName', 'Vendor', 220), t('invoiceNo', 'Against', 116), t('paymentMode', 'Mode', 110), t('bankAccount', 'Paid From', 180), m('amount', 'Amount'), t('status', 'Status', 70)],
    rows: withTotal('vendorName', ['amount']),
  },
  'debit-notes': {
    path: '/debit-notes',
    columns: [t('debitNoteNo', 'Debit Note No', 120), d('date', 'Date'), t('vendorName', 'Vendor', 220), t('invoiceNo', 'Against', 116), t('reason', 'Reason', 220), m('net', 'Net'), m('vat', 'VAT'), m('gross', 'Total')],
  },
  'ap-aging': {
    path: '/ap-aging',
    columns: [t('code', 'Code', 80), t('name', 'Vendor', 240), m('current', 'Current'), m('days1To30', '1–30 days'), m('days31To60', '31–60 days'), m('days61To90', '61–90 days'), m('over90', '90+ days'), m('unallocatedDebits', 'Unallocated Dr.'), m('total', 'Total')],
    rows: withTotal('name', ['current', 'days1To30', 'days31To60', 'days61To90', 'over90', 'unallocatedDebits', 'total']),
  },
  'expense-transactions': {
    path: '/expense-transactions',
    columns: [t('tranRef', 'Tran Ref', 100), t('invoiceNo', 'Document', 120), d('invoiceDate', 'Date'), t('vendorName', 'Vendor', 200), t('description', 'Description', 240), n('quantity', 'Qty', 50), m('amount', 'Amount'), m('vat', 'VAT'), t('kind', 'Kind', 110)],
    rows: (xs: J[]) => [...xs.map((x) => ({ ...x, kind: x.isDirectExpense ? 'Direct expense' : 'Purchase bill' })), total(xs, 'description', ['amount', 'vat'])],
  },

  items: {
    path: '/items',
    columns: [t('code', 'Code', 90), t('name', 'Item Name', 230), t('kind', 'Kind', 80), t('unit', 'Unit', 60), t('category', 'Category', 120), m('sellingPrice', 'Selling Price'), m('costPrice', 'Cost Price'), t('salesAccount', 'Sales Account', 200), t('taxCode', 'Tax', 60), t('active', 'Active', 56)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, active: x.isActive ? 'Yes' : 'No' })),
  },
  'item-kits': {
    path: '/item-kits',
    columns: [t('code', 'Kit / Item', 100), t('name', 'Name', 260), n('quantity', 'Qty', 50), m('sellingPrice', 'Unit Price'), m('amount', 'Amount')],
    rows: (xs: J[]) => xs.flatMap((k) => [{ code: k.code, name: k.name, ...meta('group') }, ...k.lines.map((l: J) => ({ code: l.itemCode, name: l.itemName, quantity: l.quantity, sellingPrice: l.sellingPrice, amount: l.amount, ...meta(undefined, 1) }))]),
  },
  units: { path: '/units', columns: [t('name', 'Unit of Measure', 180), t('active', 'Active', 60)], rows: (xs: J[]) => xs.map((x) => ({ ...x, active: x.isActive ? 'Yes' : 'No' })) },
  'item-categories': { path: '/item-categories', columns: [t('name', 'Category', 200), n('items', 'Items', 70), t('active', 'Active', 60)], rows: (xs: J[]) => xs.map((x) => ({ ...x, active: x.isActive ? 'Yes' : 'No' })) },

  leads: {
    path: '/leads',
    columns: [t('name', 'Contact', 150), t('companyName', 'Company', 200), t('mobile', 'Mobile', 130), t('source', 'Source', 90), t('stage', 'Stage', 90), m('estimatedValue', 'Est. Value'), t('assignedTo', 'Assigned', 110), t('last', 'Last Activity', 130), t('converted', 'Converted', 80)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, last: dt(x.lastActivityAtUtc), converted: x.converted ? 'Yes' : '' })),
  },
  assets: {
    path: '/fixed-assets',
    columns: [t('assetCode', 'Asset Code', 90), t('name', 'Asset', 230), t('category', 'Category', 110), d('purchaseDate', 'Purchased'), m('purchaseCost', 'Cost'), m('salvageValue', 'Salvage', 90), n('usefulLifeMonths', 'Life (m)', 64), m('accumulatedDepreciation', 'Acc. Dep.', 110), m('netBookValue', 'Net Book Value', 120), t('status', 'Status', 80)],
    rows: withTotal('name', ['purchaseCost', 'accumulatedDepreciation', 'netBookValue']),
  },

  employees: {
    path: '/employees', payroll: true,
    columns: [t('employeeCode', 'Code', 70), t('fullName', 'Full Name', 170), t('designation', 'Designation', 150), t('costCenter', 'Cost Ctr', 80), d('joiningDate', 'Joined'), m('basicSalary', 'Basic', 90), m('allowances', 'Allowances', 96), m('grossSalary', 'Gross', 96), d('visaExpiryDate', 'Visa Expiry'), d('emiratesIdExpiryDate', 'EID Expiry'), t('status', 'Status', 80)],
  },
  payroll: {
    path: '/payroll-runs', payroll: true,
    columns: [n('id', 'Run', 50), t('period', 'Period', 100), d('runDate', 'Run Date'), t('status', 'Status', 70), n('employees', 'Employees', 80), m('totalGross', 'Gross'), m('totalDeductions', 'Deductions'), m('totalNet', 'Net Pay'), t('paid', 'Paid (WPS)', 90)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, paid: x.isPaid ? `Paid ${x.paidDate ?? ''}` : 'Unpaid' })),
  },

  'trial-balance': {
    path: '/reports/trial-balance',
    columns: [t('code', 'Account', 80), t('name', 'Account Name', 250), t('type', 'Type', 80), m('openingDebit', 'Opening Dr'), m('openingCredit', 'Opening Cr'), m('periodDebit', 'Period Dr'), m('periodCredit', 'Period Cr'), m('closingDebit', 'Closing Dr', 120), m('closingCredit', 'Closing Cr', 120)],
    rows: (x: J) => [...x.rows, { name: `Total — ${x.isBalanced ? 'in balance ✓' : 'OUT OF BALANCE'} (${x.rangeLabel})`, openingDebit: x.totalOpeningDebit, openingCredit: x.totalOpeningCredit, periodDebit: x.totalPeriodDebit, periodCredit: x.totalPeriodCredit, closingDebit: x.totalClosingDebit, closingCredit: x.totalClosingCredit, ...meta(x.isBalanced ? 'grand' : 'warn') }],
  },
  pnl: {
    path: '/reports/profit-and-loss',
    columns: [t('code', 'Account', 80), t('name', 'Particulars', 300), m('period', 'Period', 130), m('ytd', 'Year to Date', 130)],
    rows: (x: J) => {
      const sec = (title: string, lines: J[], p: number, y: number): Row[] => [{ name: title, ...meta('group') }, ...lines.map((l) => ({ ...l, ...meta(undefined, 1) })), { name: `Total ${title.toLowerCase()}`, period: p, ytd: y, ...meta('total') }];
      return [
        ...sec('Operating income', x.operatingIncome, x.operatingIncomePeriod, x.operatingIncomeYtd),
        ...sec('Cost of goods sold', x.costOfGoodsSold, x.costOfGoodsSoldPeriod, x.costOfGoodsSoldYtd),
        { name: 'Gross profit', period: x.grossProfitPeriod, ytd: x.grossProfitYtd, ...meta('grand') },
        ...sec('Operating expenses', x.operatingExpense, x.operatingExpensePeriod, x.operatingExpenseYtd),
        { name: 'Operating profit', period: x.operatingProfitPeriod, ytd: x.operatingProfitYtd, ...meta('grand') },
        ...sec('Non-operating income', x.nonOperatingIncome, x.nonOperatingIncomePeriod, x.nonOperatingIncomeYtd),
        ...sec('Non-operating expenses', x.nonOperatingExpense, x.nonOperatingExpensePeriod, x.nonOperatingExpenseYtd),
        { name: `Net profit — ${x.periodName}`, period: x.netProfitPeriod, ytd: x.netProfitYtd, ...meta('grand') },
      ];
    },
  },
  'balance-sheet': {
    path: '/reports/balance-sheet',
    columns: [t('code', 'Account', 80), t('name', 'Particulars', 320), m('amount', 'Amount', 150)],
    rows: (x: J) => [
      { name: `Assets — ${x.asOf}`, ...meta('group') }, ...x.assets.map((l: J) => ({ ...l, ...meta(undefined, 1) })), { name: 'Total assets', amount: x.totalAssets, ...meta('grand') },
      { name: 'Liabilities', ...meta('group') }, ...x.liabilities.map((l: J) => ({ ...l, ...meta(undefined, 1) })), { name: 'Total liabilities', amount: x.totalLiabilities, ...meta('total') },
      { name: 'Equity', ...meta('group') }, ...x.equity.map((l: J) => ({ ...l, ...meta(undefined, 1) })), { name: 'Current year earnings', amount: x.currentYearEarnings, ...meta(undefined, 1) }, { name: 'Total equity', amount: x.totalEquity, ...meta('total') },
      { name: `Total liabilities & equity ${x.isBalanced ? '✓' : '— OUT OF BALANCE'}`, amount: x.totalLiabilitiesAndEquity, ...meta(x.isBalanced ? 'grand' : 'warn') },
    ],
  },
  'cash-flow': {
    path: '/reports/cash-flow',
    columns: [t('name', 'Particulars', 360), m('amount', 'Amount', 150)],
    rows: (x: J) => [
      { name: `Opening cash — ${x.periodName}`, amount: x.openingCash, ...meta('total') },
      ...[x.operating, x.investing, x.financing].flatMap((s: J) => [{ name: s.name, ...meta('group') }, ...s.lines.map((l: J) => ({ name: l.label, amount: l.amount, ...meta(undefined, 1) })), { name: `Net ${s.name.toLowerCase()}`, amount: s.total, ...meta('total') }]),
      { name: 'Closing cash', amount: x.closingCash, ...meta('grand') },
    ],
  },
  mis: {
    path: '/reports/segments',
    columns: [t('code', 'Segment', 100), t('name', 'Cost Center', 240), m('revenue', 'Revenue'), m('expense', 'Expense'), m('profit', 'Profit')],
    rows: (xs: J[]) => withTotal('name', ['revenue', 'expense', 'profit'])(xs.map((x) => ({ ...x, profit: x.profit ?? x.revenue - x.expense }))),
  },
  'commission-report': {
    path: '/reports/commission',
    columns: [d('invoiceDate', 'Date'), t('invoiceNo', 'Invoice', 116), t('customerName', 'Customer', 200), t('itemName', 'Item', 180), m('grossProfit', 'Gross Profit'), t('eligible', 'Eligible', 64), t('agentName', 'Agent', 120), m('agentCommission', 'Agent Comm.'), t('employeeName', 'Employee', 130), m('employeeCommission', 'Employee Comm.')],
    rows: (xs: J[]) => [...xs.map((x) => ({ ...x, eligible: x.isEligible ? 'Yes' : 'No' })), total(xs, 'customerName', ['grossProfit', 'agentCommission', 'employeeCommission'])],
  },
  'audit-log': {
    path: '/reports/reassignment-audit',
    columns: [t('when', 'Changed At', 130), t('changedBy', 'By', 180), t('customerName', 'Customer', 200), d('effectiveFrom', 'Effective'), t('from', 'From', 200), t('to', 'To', 200)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, when: dt(x.changedAtUtc), from: [x.previousEmployeeName, x.previousAgentName].filter(Boolean).join(' / '), to: [x.newEmployeeName, x.newAgentName].filter(Boolean).join(' / ') })),
  },

  'fiscal-periods': {
    path: '/fiscal-periods',
    columns: [t('name', 'Period', 110), t('year', 'Year', 60), n('periodNo', 'No', 44), d('startDate', 'Start'), d('endDate', 'End'), t('state', 'Status', 80)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, state: x.isClosed ? 'Closed' : 'Open', ...(x.isClosed ? meta('muted') : {}) })),
  },
  'cost-centers': { path: '/cost-centers', columns: [t('code', 'Code', 90), t('name', 'Cost Center', 240), t('active', 'Active', 60)], rows: (xs: J[]) => xs.map((x) => ({ ...x, active: x.isActive ? 'Yes' : 'No' })) },
  currencies: {
    path: '/currencies',
    columns: [t('code', 'Code', 60), t('name', 'Currency', 200), { key: 'rateToBase', label: 'Rate to Base', width: 100, type: 'number' }, t('active', 'Active', 60), t('updated', 'Updated', 130)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, active: x.isActive ? 'Yes' : 'No', updated: dt(x.updatedAtUtc) })),
  },
  'tax-configuration': {
    path: '/tax-codes',
    columns: [t('code', 'Tax Code', 80), t('description', 'Description', 220), pct('rate', 'Rate', 60), t('taxType', 'Type', 110), d('effectiveFrom', 'Effective'), t('outputAccount', 'Output Account', 180), t('inputAccount', 'Input Account', 180)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, rate: x.rate > 1 ? x.rate / 100 : x.rate })),
  },
  'service-kits': {
    path: '/service-kits',
    columns: [t('name', 'Kit / Line', 280), m('govtFee', 'Govt Fee', 90), m('centerFee', 'Center Fee', 90), m('bankCharge', 'Bank Charge', 90), pct('vatRate', 'VAT', 60)],
    rows: (xs: J[]) => xs.flatMap((k) => [{ name: k.name, ...meta('group') }, ...k.lines.map((l: J) => ({ name: l.description, govtFee: l.govtFee, centerFee: l.centerFee, bankCharge: l.bankCharge, vatRate: l.vatRate > 1 ? l.vatRate / 100 : l.vatRate, ...meta(undefined, 1) }))]),
  },
  users: {
    path: '/team', admin: true,
    columns: [t('displayName', 'Name', 180), t('email', 'Email', 230), t('role', 'Role', 110), t('payroll', 'Payroll Access', 110)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, payroll: x.canAccessPayroll ? 'Yes' : 'No' })),
  },
  'company-setup': {
    path: '/company-profile',
    columns: [t('field', 'Setting', 240), t('value', 'Value', 460)],
    rows: (c: J) => [
      ['Legal name', c.legalName], ['Trade name', c.tradeName], ['Company code', c.companyCode], ['Licence', [c.licenseNumber, c.licenseExpiryDate && `expires ${c.licenseExpiryDate}`].filter(Boolean).join(' · ')],
      ['TRN', c.trnNumber], ['VAT registered', c.vatRegistered ? 'Yes' : 'No'], ['Address', [c.city, c.addressEmirate, c.poBox && `P.O. Box ${c.poBox}`, c.addressCountry].filter(Boolean).join(', ')],
      ['Phone', c.phone], ['Base currency', c.baseCurrency], ['Financial year', [c.financialYearStart, c.financialYearEnd].filter(Boolean).join(' → ')],
      ['Approval workflow', c.approvalWorkflowEnabled ? 'On' : 'Off'], ['PRO-Service mode', c.proServiceModeEnabled ? 'On' : 'Off'],
      ['Primary bank', c.bank ? `${c.bank.bankName} · ${c.bank.accountName} · IBAN ${c.bank.iban ?? ''}` : '—'],
    ].map(([field, value]) => ({ field, value: value ?? '—' })),
  },
  'commission-config': {
    path: '/commission-config',
    columns: [t('kind', 'Rule', 160), t('key', 'Applies To', 220), t('value', 'Rate / Range', 220), t('active', 'Active', 60)],
    rows: (x: J) => [
      { kind: 'CATEGORY BASE RATES', ...meta('group') },
      ...x.categoryRates.map((r: J) => ({ kind: 'Category', key: `Category #${r.itemCategoryId}`, value: `${r.baseCommissionPercent}%`, active: r.isActive ? 'Yes' : 'No', ...meta(undefined, 1) })),
      { kind: 'PROFIT SLABS', ...meta('group') },
      ...x.profitSlabs.map((r: J) => ({ kind: 'Slab', key: `${r.grossProfitFrom} – ${r.grossProfitTo ?? '∞'}`, value: `+${r.commissionAdditionPercent}%`, active: r.isActive ? 'Yes' : 'No', ...meta(undefined, 1) })),
      { kind: 'POSITION MULTIPLIERS', ...meta('group') },
      ...x.positionRates.map((r: J) => ({ kind: 'Position', key: r.position, value: `× ${r.rateMultiplier}`, active: r.isActive ? 'Yes' : 'No', ...meta(undefined, 1) })),
    ],
  },
  'custom-fields': {
    path: '/custom-fields',
    columns: [t('module', 'Entity', 120), t('label', 'Field', 220), t('fieldType', 'Type', 100), t('dropdownOptionsCsv', 'Options', 240), t('required', 'Required', 70), t('active', 'Active', 60)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, required: x.isRequired ? 'Yes' : 'No', active: x.isActive ? 'Yes' : 'No' })),
  },
  // ── Industry modules ─────────────────────────────────────────────────
  'industry-modules': {
    path: '/modules/profile',
    columns: [t('field', 'Setting', 260), t('value', 'Value', 420)],
    rows: (x: J) => [
      { field: 'Industry', value: x.industryName, ...meta('total') },
      { field: 'Enabled modules', value: (x.modules as string[]).join(', ') || 'none — core accounting only' },
      { field: 'Jobs are called', value: x.jobLabel },
      { field: 'GL MAPPING (account ids)', ...meta('group') },
      ...[['Inventory control', 'inventoryAccountId'], ['Cost of goods sold', 'cogsAccountId'], ['Goods-received clearing', 'stockClearingAccountId'], ['Stock adjustments', 'stockAdjustmentAccountId'], ['Production overhead absorbed', 'conversionCostAccountId'], ['Fleet running costs', 'fleetExpenseAccountId']]
        .map(([label, k]) => ({ field: label, value: x.profile?.[k] ? `#${x.profile[k]}` : '— not mapped —', ...meta(undefined, 1) })),
    ],
  },
  'stock-on-hand': {
    path: '/inventory/stock',
    columns: [t('itemCode', 'Item', 90), t('itemName', 'Description', 220), t('unit', 'Unit', 60), t('warehouse', 'Warehouse', 90), n('quantity', 'On Hand', 90), m('avgCost', 'Avg Cost', 100), m('value', 'Value', 120), n('itemTotal', 'Item Total', 90), n('reorderLevel', 'Reorder At', 90), t('flag', 'Status', 110)],
    rows: (xs: J[]) => [...xs.map((x) => ({ ...x, flag: x.belowReorder ? 'Reorder now' : 'OK', ...(x.belowReorder ? meta('warn') : {}) })), total(xs, 'itemName', ['value'])],
  },
  'stock-moves': {
    path: '/inventory/moves',
    columns: [t('moveNo', 'Move No', 120), t('type', 'Type', 130), d('date', 'Date'), t('reference', 'Reference', 120), t('narration', 'Narration', 220), n('lines', 'Lines', 56), m('value', 'Value'), t('voucherNo', 'GL Voucher', 120), t('createdBy', 'By', 140)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, lines: x.lines.length })),
  },
  'stock-valuation': {
    path: '/inventory/valuation',
    columns: [t('itemCode', 'Item', 90), t('itemName', 'Description', 240), n('quantity', 'Quantity', 100), m('avgCost', 'Avg Cost', 110), m('value', 'Value', 130)],
    rows: (x: J) => [
      ...x.rows,
      { itemName: 'Stock ledger value', value: x.totalValue, ...meta('grand') },
      ...(x.glInventoryBalance == null ? [{ itemName: 'Map the inventory account to reconcile with the GL', ...meta('muted') }] : [
        { itemName: 'GL inventory account balance', value: x.glInventoryBalance, ...meta('total') },
        { itemName: x.difference === 0 ? 'Difference — reconciled ✓' : 'Difference (opening balances or manual journals to the inventory account)', value: x.difference, ...meta(x.difference === 0 ? 'total' : 'warn') },
      ]),
    ],
  },
  reorder: {
    path: '/inventory/reorder',
    columns: [t('itemCode', 'Item', 90), t('itemName', 'Description', 240), n('onHand', 'On Hand', 100), n('reorderLevel', 'Reorder Level', 110)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, ...meta('warn') })),
  },
  warehouses: {
    path: '/inventory/warehouses',
    columns: [n('id', 'Id', 50), t('code', 'Code', 90), t('name', 'Warehouse', 260), t('active', 'Active', 60)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, active: x.isActive ? 'Yes' : 'No' })),
  },
  boms: {
    path: '/manufacturing/boms',
    columns: [t('code', 'BOM / Component', 110), t('name', 'Name', 240), n('quantity', 'Qty', 70), t('outputItem', 'Produces', 180), m('conversionCost', 'Conversion / batch', 130), m('standardMaterialCost', 'Std Material', 120), m('standardUnitCost', 'Std Unit Cost', 120)],
    rows: (xs: J[]) => xs.flatMap((b) => [
      { id: b.id, code: b.code, name: b.name, quantity: b.outputQuantity, outputItem: b.outputItem, conversionCost: b.conversionCost, standardMaterialCost: b.standardMaterialCost, standardUnitCost: b.standardUnitCost, ...meta('group') },
      ...b.lines.map((l: J) => ({ code: l.itemCode, name: l.itemName, quantity: l.quantity, ...meta(undefined, 1) })),
    ]),
  },
  'production-orders': {
    path: '/manufacturing/orders',
    columns: [t('orderNo', 'Order No', 120), t('bom', 'BOM', 90), t('product', 'Product', 200), n('quantity', 'Qty', 70), d('plannedDate', 'Planned'), t('status', 'Status', 90), t('route', 'From → To', 110), m('materialCost', 'Material'), m('conversionCost', 'Conversion'), m('unitCost', 'Unit Cost'), t('voucherNo', 'GL Voucher', 120)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, route: `${x.from ?? ''} → ${x.to ?? ''}`, ...(x.status === 'Cancelled' ? meta('muted') : {}) })),
  },
  requirements: {
    path: '/manufacturing/requirements',
    columns: [t('itemCode', 'Component', 100), t('itemName', 'Description', 240), t('warehouse', 'Warehouse', 90), n('required', 'Required', 100), n('onHand', 'On Hand', 100), n('shortage', 'Shortage', 100)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, ...(x.shortage > 0 ? meta('warn') : {}) })),
  },
  jobs: {
    path: '/jobs',
    columns: [t('jobNo', 'Job No', 120), t('title', 'Title', 220), t('type', 'Type', 80), t('status', 'Status', 90), t('customer', 'Customer', 170), d('openedDate', 'Opened'), m('budget', 'Budget'), m('revenue', 'Revenue'), m('cost', 'Cost'), m('margin', 'Margin'), n('marginPct', 'Margin %', 80), n('budgetUsedPct', 'Budget Used %', 100)],
    rows: (xs: J[]) => [...xs.map((x) => ({ ...x, ...(x.overBudget ? meta('warn') : x.status === 'Cancelled' ? meta('muted') : {}) })), total(xs, 'title', ['budget', 'revenue', 'cost', 'margin'])],
  },
  shipments: {
    path: '/jobs',
    columns: [t('jobNo', 'Shipment', 120), t('mode', 'Mode', 70), t('direction', 'Dir.', 70), t('origin', 'Origin', 90), t('destination', 'Destination', 100), t('carrier', 'Carrier', 120), t('awbBl', 'AWB / BL', 140), t('containerNo', 'Container', 120), n('packages', 'Pkgs', 60), n('weightKg', 'Weight kg', 90), d('etd', 'ETD'), d('eta', 'ETA'), t('status', 'Status', 90)],
    rows: (xs: J[]) => xs.filter((x) => x.type === 'Shipment'),
  },
  vehicles: {
    path: '/fleet/vehicles',
    columns: [n('id', 'Id', 50), t('plateNo', 'Plate', 120), t('type', 'Type', 90), n('capacityKg', 'Capacity kg', 100), n('trips', 'Trips', 60), n('distanceKm', 'Km', 80), n('fuelLitres', 'Fuel L', 80), m('runningCost', 'Running Cost'), m('costPerKm', 'Cost / km', 90), n('kmPerLitre', 'Km / L', 70)],
  },
  trips: {
    path: '/fleet/trips',
    columns: [t('tripNo', 'Trip No', 120), d('date', 'Date'), t('vehicle', 'Vehicle', 110), t('job', 'Job', 120), t('driver', 'Driver', 110), t('from', 'From', 120), t('to', 'To', 120), n('distanceKm', 'Km', 70), m('fuelCost', 'Fuel', 90), m('tolls', 'Tolls', 80), m('otherCost', 'Other', 80), m('total', 'Total', 100), t('voucherNo', 'GL Voucher', 120)],
    rows: withTotal('to', ['distanceKm', 'fuelCost', 'tolls', 'otherCost', 'total']),
  },
  fuel: {
    path: '/fleet/vehicles',
    columns: [t('plateNo', 'Vehicle', 120), n('distanceKm', 'Km', 90), n('fuelLitres', 'Fuel L', 90), n('kmPerLitre', 'Km / L', 80), m('runningCost', 'Running Cost', 120), m('costPerKm', 'Cost / km', 100)],
    rows: withTotal('plateNo', ['distanceKm', 'fuelLitres', 'runningCost']),
  },
  insights: {
    path: '/ai/insights',
    columns: [t('severity', 'Severity', 80), t('area', 'Area', 120), t('finding', 'Finding', 520), t('action', 'Suggested Action', 260)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, ...(x.severity === 'High' || x.severity === 'Medium' ? meta('warn') : {}) })),
  },
  'ai-guardrails': {
    path: '/ai/log', admin: true,
    columns: [t('when', 'When', 130), t('user', 'User', 160), t('question', 'Question', 420), t('toolsUsed', 'Tools Used', 240), n('tokens', 'Tokens', 80), t('ok', 'OK', 40)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, when: dt(x.createdAtUtc), tokens: x.inputTokens + x.outputTokens, ok: x.succeeded ? '✓' : '✗' })),
  },
  'leave-requests': {
    path: '/leave-requests', payroll: true,
    columns: [n('id', 'Req', 50), t('employee', 'Employee', 180), t('type', 'Type', 80), d('startDate', 'From'), d('endDate', 'To'), n('days', 'Days', 50), t('reason', 'Reason', 220), t('status', 'Status', 90), t('decisionBy', 'Decided By', 150)],
    rows: (xs: J[]) => xs.map((x) => ({ ...x, ...(x.status === 'Pending' ? meta('warn') : {}) })),
  },
};

// Extra reports reachable from the Reports ▾ menu.
Object.assign(LIVE, REPORT_LIVE);
for (const [id, dm] of Object.entries(REPORT_DATES)) if (LIVE[id]) LIVE[id].dates = dm;
