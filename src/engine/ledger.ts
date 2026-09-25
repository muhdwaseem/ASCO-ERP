// Posting engine + read models. Mirrors C-ERP's JournalPoster design:
// every document builds ONE balanced voucher through postVoucher(), which validates
// period-open, postable accounts and debits = credits BEFORE anything is written.
// The document and its voucher share a number (INV-2026-0001) and are saved together.

import type {
  DocLine, JournalLine, JournalVoucher, LedgerState, NoteDoc, Settlement, TradeDoc, DirectExpense, VoucherType, PayrollRun,
} from './types';

export const ACC = {
  BANK: '1260-01', PETTY: '1260-02', AR: '1210-01', VAT_IN: '1240-01',
  FURNITURE: '1100-01', VEHICLES: '1100-02', IT: '1100-03', ACC_DEP: '1150-01',
  AP: '2200-01', ACCRUED_SAL: '2210-01', VAT_OUT: '2220-01', EOSB: '2110-01',
  CAPITAL: '3100-01', RETAINED: '3300-01',
  REV: '4100-01', PRO_REV: '4100-02', DISCOUNT: '4150-01', OTHER_INC: '4200-01',
  COST_SERV: '5100-01', SALARY: '5200-01', ALLOW: '5200-02', RENT: '5300-01', UTIL: '5300-02',
  GOVT: '5300-03', SOFTWARE: '5300-04', MARKETING: '5400-01', BANK_CHG: '5500-01', DEP_EXP: '5600-01', MISC: '5700-01',
} as const;

export class PostingError extends Error {}

export const r2 = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100;

export function lineTotals(l: DocLine) {
  const net = r2(l.quantity * l.unitPrice);
  const vat = r2(net * l.vatRate);
  return { net, vat, gross: r2(net + vat) };
}

export function docTotals(lines: DocLine[]) {
  return lines.reduce(
    (t, l) => {
      const x = lineTotals(l);
      return { net: r2(t.net + x.net), vat: r2(t.vat + x.vat), gross: r2(t.gross + x.gross) };
    },
    { net: 0, vat: 0, gross: 0 },
  );
}

/** Next number per (prefix, year) — continues from the highest existing suffix. */
export function nextNo(existing: string[], prefix: string, date: string) {
  const year = date.slice(0, 4);
  const head = `${prefix}-${year}-`;
  const max = existing
    .filter((n) => n.startsWith(head))
    .reduce((m, n) => Math.max(m, Number(n.slice(head.length)) || 0), 0);
  return `${head}${String(max + 1).padStart(4, '0')}`;
}

export function allDocNumbers(s: LedgerState) {
  return [
    ...s.vouchers.map((v) => v.voucherNo),
    ...s.estimates.map((e) => e.no),
    ...s.deliveryNotes.map((d) => d.no),
  ];
}

/** The single gate every posting goes through. Throws; never half-writes. */
export function validateVoucher(s: LedgerState, v: JournalVoucher) {
  if (v.lines.length < 2) throw new PostingError('A voucher needs at least two lines.');
  const period = s.periods.find((p) => v.date >= p.startDate && v.date <= p.endDate);
  if (!period) throw new PostingError(`No fiscal period covers ${v.date}.`);
  if (period.isClosed) throw new PostingError(`Fiscal period ${period.name} is closed.`);
  let dr = 0;
  let cr = 0;
  for (const l of v.lines) {
    const a = s.accounts.find((x) => x.code === l.account);
    if (!a) throw new PostingError(`Unknown account ${l.account}.`);
    if (!a.isPostable) throw new PostingError(`${a.code} ${a.name} is a header account — post to a child account.`);
    if (l.debit < 0 || l.credit < 0) throw new PostingError('Amounts cannot be negative.');
    if (l.debit > 0 && l.credit > 0) throw new PostingError('A line is either a debit or a credit, not both.');
    dr = r2(dr + l.debit);
    cr = r2(cr + l.credit);
  }
  if (dr === 0) throw new PostingError('Voucher total is zero.');
  if (dr !== cr) throw new PostingError(`Out of balance: debits ${dr.toFixed(2)} ≠ credits ${cr.toFixed(2)}.`);
}

function makeVoucher(
  s: LedgerState, type: VoucherType, prefix: string, date: string, narration: string,
  lines: JournalLine[], user: string, reference?: string, forcedNo?: string,
): JournalVoucher {
  const cleaned = lines.filter((l) => l.debit > 0 || l.credit > 0).map((l) => ({ ...l, debit: r2(l.debit), credit: r2(l.credit) }));
  const v: JournalVoucher = {
    voucherNo: forcedNo ?? nextNo(allDocNumbers(s), prefix, date),
    type, date, narration, reference, status: 'Posted', createdBy: user, lines: cleaned,
  };
  validateVoucher(s, v);
  return v;
}

function audit(s: LedgerState, user: string, action: string, entity: string, detail: string) {
  s.audit.unshift({ at: new Date().toISOString().slice(0, 16).replace('T', ' '), user, action, entity, detail });
}

// ---------------------------------------------------------------- subledgers

export function invoiceOutstanding(s: LedgerState, no: string) {
  const inv = s.salesInvoices.find((i) => i.no === no);
  if (!inv) return 0;
  const paid = s.receipts.filter((r) => r.againstDoc === no).reduce((t, r) => t + r.amount, 0);
  const credited = s.creditNotes.filter((c) => c.againstDoc === no).reduce((t, c) => t + docTotals(c.lines).gross, 0);
  return r2(docTotals(inv.lines).gross - paid - credited);
}

export function billOutstanding(s: LedgerState, no: string) {
  const bill = s.purchaseInvoices.find((i) => i.no === no);
  if (!bill) return 0;
  const paid = s.payments.filter((r) => r.againstDoc === no).reduce((t, r) => t + r.amount, 0);
  const debited = s.debitNotes.filter((c) => c.againstDoc === no).reduce((t, c) => t + docTotals(c.lines).gross, 0);
  return r2(docTotals(bill.lines).gross - paid - debited);
}

// ---------------------------------------------------------------- document posting

export function postJournal(s: LedgerState, date: string, narration: string, lines: JournalLine[], user: string, type: VoucherType = 'JV', prefix = 'JV') {
  const v = makeVoucher(s, type, prefix, date, narration, lines, user);
  s.vouchers.push(v);
  audit(s, user, 'Posted', 'Journal Voucher', `${v.voucherNo} · ${narration}`);
  return v;
}

export function postSalesInvoice(s: LedgerState, doc: Omit<TradeDoc, 'no' | 'status'>, user: string) {
  const t = docTotals(doc.lines);
  const lines: JournalLine[] = [
    { account: ACC.AR, debit: t.gross, credit: 0, party: doc.party, narration: 'Trade receivable' },
    ...doc.lines.map((l) => ({ account: l.account, debit: 0, credit: lineTotals(l).net, narration: l.description, costCenter: l.costCenter })),
    { account: ACC.VAT_OUT, debit: 0, credit: t.vat, narration: 'Output VAT' },
  ];
  const v = makeVoucher(s, 'INV', 'INV', doc.date, doc.narration ?? `Sales invoice — ${doc.party}`, lines, user);
  s.vouchers.push(v);
  s.salesInvoices.push({ ...doc, no: v.voucherNo, status: 'Posted' });
  audit(s, user, 'Posted', 'Sales Invoice', `${v.voucherNo} · ${doc.party} · ${t.gross.toFixed(2)}`);
  return v.voucherNo;
}

export function postPurchaseInvoice(s: LedgerState, doc: Omit<TradeDoc, 'no' | 'status'>, user: string) {
  const t = docTotals(doc.lines);
  const lines: JournalLine[] = [
    ...doc.lines.map((l) => ({ account: l.account, debit: lineTotals(l).net, credit: 0, narration: l.description, costCenter: l.costCenter })),
    { account: ACC.VAT_IN, debit: t.vat, credit: 0, narration: 'Input VAT recoverable' },
    { account: ACC.AP, debit: 0, credit: t.gross, party: doc.party, narration: 'Trade payable' },
  ];
  const v = makeVoucher(s, 'PINV', 'PINV', doc.date, doc.narration ?? `Purchase invoice — ${doc.party}`, lines, user);
  s.vouchers.push(v);
  s.purchaseInvoices.push({ ...doc, no: v.voucherNo, status: 'Posted' });
  audit(s, user, 'Posted', 'Purchase Invoice', `${v.voucherNo} · ${doc.party} · ${t.gross.toFixed(2)}`);
  return v.voucherNo;
}

export function postReceipt(s: LedgerState, r: Omit<Settlement, 'no'>, user: string) {
  if (r.againstDoc) {
    const out = invoiceOutstanding(s, r.againstDoc);
    if (r.amount > out + 0.001) throw new PostingError(`Receipt ${r.amount.toFixed(2)} exceeds ${r.againstDoc} outstanding ${out.toFixed(2)}.`);
  }
  const v = makeVoucher(s, 'RV', 'RV', r.date, `Receipt from ${r.party}${r.againstDoc ? ` against ${r.againstDoc}` : ' on account'}`, [
    { account: r.bankAccount, debit: r.amount, credit: 0 },
    { account: ACC.AR, debit: 0, credit: r.amount, party: r.party },
  ], user, r.reference);
  s.vouchers.push(v);
  s.receipts.push({ ...r, no: v.voucherNo });
  audit(s, user, 'Posted', 'Receipt Voucher', `${v.voucherNo} · ${r.party} · ${r.amount.toFixed(2)}`);
  return v.voucherNo;
}

export function postPayment(s: LedgerState, p: Omit<Settlement, 'no'>, user: string) {
  if (p.againstDoc) {
    const out = billOutstanding(s, p.againstDoc);
    if (p.amount > out + 0.001) throw new PostingError(`Payment ${p.amount.toFixed(2)} exceeds ${p.againstDoc} outstanding ${out.toFixed(2)}.`);
  }
  const v = makeVoucher(s, 'PV', 'PV', p.date, `Payment to ${p.party}${p.againstDoc ? ` against ${p.againstDoc}` : ' on account'}`, [
    { account: ACC.AP, debit: p.amount, credit: 0, party: p.party },
    { account: p.bankAccount, debit: 0, credit: p.amount },
  ], user, p.reference);
  s.vouchers.push(v);
  s.payments.push({ ...p, no: v.voucherNo });
  audit(s, user, 'Posted', 'Payment Voucher', `${v.voucherNo} · ${p.party} · ${p.amount.toFixed(2)}`);
  return v.voucherNo;
}

export function postCreditNote(s: LedgerState, n: Omit<NoteDoc, 'no'>, user: string) {
  const t = docTotals(n.lines);
  if (n.againstDoc && t.gross > invoiceOutstanding(s, n.againstDoc) + 0.001) throw new PostingError('Credit note exceeds invoice outstanding.');
  const v = makeVoucher(s, 'CN', 'CN', n.date, `Credit note — ${n.reason}`, [
    ...n.lines.map((l) => ({ account: l.account, debit: lineTotals(l).net, credit: 0, narration: l.description })),
    { account: ACC.VAT_OUT, debit: t.vat, credit: 0 },
    { account: ACC.AR, debit: 0, credit: t.gross, party: n.party },
  ], user);
  s.vouchers.push(v);
  s.creditNotes.push({ ...n, no: v.voucherNo });
  audit(s, user, 'Posted', 'Credit Note', `${v.voucherNo} · ${n.party}`);
  return v.voucherNo;
}

export function postDebitNote(s: LedgerState, n: Omit<NoteDoc, 'no'>, user: string) {
  const t = docTotals(n.lines);
  if (n.againstDoc && t.gross > billOutstanding(s, n.againstDoc) + 0.001) throw new PostingError('Debit note exceeds bill outstanding.');
  const v = makeVoucher(s, 'DN', 'DN', n.date, `Debit note — ${n.reason}`, [
    { account: ACC.AP, debit: t.gross, credit: 0, party: n.party },
    ...n.lines.map((l) => ({ account: l.account, debit: 0, credit: lineTotals(l).net, narration: l.description })),
    { account: ACC.VAT_IN, debit: 0, credit: t.vat },
  ], user);
  s.vouchers.push(v);
  s.debitNotes.push({ ...n, no: v.voucherNo });
  audit(s, user, 'Posted', 'Debit Note', `${v.voucherNo} · ${n.party}`);
  return v.voucherNo;
}

export function postExpense(s: LedgerState, e: Omit<DirectExpense, 'no'>, user: string) {
  const t = docTotals(e.lines);
  const v = makeVoucher(s, 'EXP', 'EXP', e.date, e.narration, [
    ...e.lines.map((l) => ({ account: l.account, debit: lineTotals(l).net, credit: 0, narration: l.description, costCenter: l.costCenter })),
    { account: ACC.VAT_IN, debit: t.vat, credit: 0 },
    { account: e.bankAccount, debit: 0, credit: t.gross },
  ], user);
  s.vouchers.push(v);
  s.expenses.push({ ...e, no: v.voucherNo });
  audit(s, user, 'Posted', 'Expense', `${v.voucherNo} · ${t.gross.toFixed(2)}`);
  return v.voucherNo;
}

export function payrollTotals(s: LedgerState, run: Pick<PayrollRun, 'employees'>) {
  return run.employees.reduce(
    (t, code) => {
      const e = s.employees.find((x) => x.employeeCode === code)!;
      const allow = e.housingAllowance + e.transportAllowance + e.otherAllowance;
      return { basic: t.basic + e.basicSalary, allowances: t.allowances + allow, gross: t.gross + e.basicSalary + allow };
    },
    { basic: 0, allowances: 0, gross: 0 },
  );
}

export function postPayroll(s: LedgerState, period: string, runDate: string, user: string, paid: boolean) {
  const employees = s.employees.filter((e) => e.status === 'Active').map((e) => e.employeeCode);
  const t = payrollTotals(s, { employees });
  const v = makeVoucher(s, 'PAY', 'PAY', runDate, `Payroll ${period}`, [
    { account: ACC.SALARY, debit: t.basic, credit: 0 },
    { account: ACC.ALLOW, debit: t.allowances, credit: 0 },
    { account: ACC.ACCRUED_SAL, debit: 0, credit: t.gross },
  ], user);
  s.vouchers.push(v);
  s.payrollRuns.push({ no: v.voucherNo, period, runDate, isPaid: false, employees });
  if (paid) markPayrollPaid(s, v.voucherNo, runDate, user);
  audit(s, user, 'Posted', 'Payroll Run', `${v.voucherNo} · ${period} · ${t.gross.toFixed(2)}`);
  return v.voucherNo;
}

export function markPayrollPaid(s: LedgerState, runNo: string, date: string, user: string) {
  const run = s.payrollRuns.find((r) => r.no === runNo);
  if (!run || run.isPaid) return;
  const t = payrollTotals(s, run);
  postJournal(s, date, `WPS salary transfer ${run.period}`, [
    { account: ACC.ACCRUED_SAL, debit: t.gross, credit: 0 },
    { account: ACC.BANK, debit: 0, credit: t.gross },
  ], user);
  run.isPaid = true;
}

export function monthlyDepreciation(a: { purchaseCost: number; salvageValue: number; usefulLifeMonths: number }) {
  return r2((a.purchaseCost - a.salvageValue) / a.usefulLifeMonths);
}

export function runDepreciation(s: LedgerState, periodEnd: string, user: string) {
  const lines: JournalLine[] = [];
  for (const a of s.assets.filter((x) => x.status === 'Active' && x.purchaseDate <= periodEnd)) {
    const m = monthlyDepreciation(a);
    lines.push({ account: ACC.DEP_EXP, debit: m, credit: 0, narration: a.name });
    lines.push({ account: ACC.ACC_DEP, debit: 0, credit: m, narration: a.assetCode });
  }
  if (!lines.length) throw new PostingError('No active assets to depreciate.');
  const v = makeVoucher(s, 'DEP', 'DEP', periodEnd, `Depreciation run ${periodEnd.slice(0, 7)}`, lines, user);
  s.vouchers.push(v);
  audit(s, user, 'Posted', 'Depreciation', v.voucherNo);
  return v.voucherNo;
}

// ---------------------------------------------------------------- read models

export function accountName(s: LedgerState, code: string) {
  return s.accounts.find((a) => a.code === code)?.name ?? '';
}

export function partyName(s: LedgerState, code?: string) {
  if (!code) return '';
  return (s.customers.find((c) => c.code === code) ?? s.vendors.find((c) => c.code === code))?.name ?? code;
}

export function balances(s: LedgerState, opts: { from?: string; to?: string } = {}) {
  const map = new Map<string, { debit: number; credit: number }>();
  for (const v of s.vouchers) {
    if (v.status !== 'Posted') continue;
    if (opts.from && v.date < opts.from) continue;
    if (opts.to && v.date > opts.to) continue;
    for (const l of v.lines) {
      const b = map.get(l.account) ?? { debit: 0, credit: 0 };
      b.debit = r2(b.debit + l.debit);
      b.credit = r2(b.credit + l.credit);
      map.set(l.account, b);
    }
  }
  return map;
}

/** Natural-side balance: debit-normal for assets/expenses, credit-normal otherwise. */
export function naturalBalance(s: LedgerState, code: string, b: { debit: number; credit: number }) {
  const t = s.accounts.find((a) => a.code === code)?.type;
  return t === 'Asset' || t === 'Expense' ? r2(b.debit - b.credit) : r2(b.credit - b.debit);
}

export function trialBalance(s: LedgerState, to?: string) {
  const b = balances(s, { to });
  return s.accounts
    .filter((a) => a.isPostable && b.has(a.code))
    .map((a) => {
      const x = b.get(a.code)!;
      const net = r2(x.debit - x.credit);
      return { code: a.code, name: a.name, type: a.type, debit: net > 0 ? net : 0, credit: net < 0 ? -net : 0 };
    })
    .filter((r) => r.debit || r.credit);
}

export function pnl(s: LedgerState, from: string, to: string) {
  const b = balances(s, { from, to });
  const section = (type: 'Revenue' | 'Expense') =>
    s.accounts
      .filter((a) => a.isPostable && a.type === type && b.has(a.code))
      .map((a) => ({ code: a.code, name: a.name, category: s.accounts.find((p) => p.code === a.parent)?.name ?? '', amount: naturalBalance(s, a.code, b.get(a.code)!) }))
      .filter((r) => r.amount !== 0);
  const revenue = section('Revenue');
  const expenses = section('Expense');
  const totalRev = r2(revenue.reduce((t, r) => t + r.amount, 0));
  const totalExp = r2(expenses.reduce((t, r) => t + r.amount, 0));
  return { revenue, expenses, totalRev, totalExp, net: r2(totalRev - totalExp) };
}

export function balanceSheet(s: LedgerState, asOf: string) {
  const b = balances(s, { to: asOf });
  const section = (type: 'Asset' | 'Liability' | 'Equity') =>
    s.accounts
      .filter((a) => a.isPostable && a.type === type && b.has(a.code))
      .map((a) => ({ code: a.code, name: a.name, category: s.accounts.find((p) => p.code === a.parent)?.category ?? '', amount: naturalBalance(s, a.code, b.get(a.code)!) }))
      .filter((r) => r.amount !== 0);
  const profit = pnl(s, '0000-01-01', asOf).net;
  const assets = section('Asset');
  const liabilities = section('Liability');
  const equity = section('Equity');
  const sum = (rows: { amount: number }[]) => r2(rows.reduce((t, r) => t + r.amount, 0));
  return { assets, liabilities, equity, profit, totalAssets: sum(assets), totalLiab: sum(liabilities), totalEquity: r2(sum(equity) + profit) };
}

export function generalLedger(s: LedgerState, account?: string) {
  const rows: { date: string; voucherNo: string; account: string; name: string; narration: string; party: string; debit: number; credit: number; balance: number }[] = [];
  const running = new Map<string, number>();
  const sorted = [...s.vouchers].sort((a, b) => (a.date + a.voucherNo).localeCompare(b.date + b.voucherNo));
  for (const v of sorted) {
    for (const l of v.lines) {
      if (account && l.account !== account) continue;
      const bal = r2((running.get(l.account) ?? 0) + l.debit - l.credit);
      running.set(l.account, bal);
      rows.push({ date: v.date, voucherNo: v.voucherNo, account: l.account, name: accountName(s, l.account), narration: l.narration ?? v.narration, party: partyName(s, l.party), debit: l.debit, credit: l.credit, balance: bal });
    }
  }
  return rows;
}

const daysBetween = (a: string, b: string) => Math.floor((Date.parse(b) - Date.parse(a)) / 86_400_000);

export function aging(s: LedgerState, side: 'AR' | 'AP', asOf: string) {
  const docs = side === 'AR' ? s.salesInvoices : s.purchaseInvoices;
  const out = side === 'AR' ? invoiceOutstanding : billOutstanding;
  const parties = side === 'AR' ? s.customers : s.vendors;
  return parties
    .map((p) => {
      const row = { code: p.code, name: p.name, current: 0, d30: 0, d60: 0, d90: 0, d90p: 0, total: 0 };
      for (const d of docs.filter((x) => x.party === p.code)) {
        const o = out(s, d.no);
        if (o <= 0) continue;
        const late = daysBetween(d.dueDate, asOf);
        if (late <= 0) row.current += o;
        else if (late <= 30) row.d30 += o;
        else if (late <= 60) row.d60 += o;
        else if (late <= 90) row.d90 += o;
        else row.d90p += o;
        row.total = r2(row.total + o);
      }
      return row;
    })
    .filter((r) => r.total > 0);
}

export function cashFlow(s: LedgerState, from: string, to: string) {
  const bankCodes = s.accounts.filter((a) => a.parent === '1260').map((a) => a.code);
  const buckets = { Operating: new Map<string, number>(), Investing: new Map<string, number>(), Financing: new Map<string, number>() };
  for (const v of s.vouchers) {
    if (v.date < from || v.date > to) continue;
    const bankMove = r2(v.lines.filter((l) => bankCodes.includes(l.account)).reduce((t, l) => t + l.debit - l.credit, 0));
    if (!bankMove) continue;
    const contra = v.lines.find((l) => !bankCodes.includes(l.account));
    const acct = s.accounts.find((a) => a.code === contra?.account);
    const bucket = acct?.parent === '1100' ? 'Investing' : acct?.type === 'Equity' || acct?.parent === '2100' ? 'Financing' : 'Operating';
    const label = v.type === 'RV' ? 'Receipts from customers' : v.type === 'PV' ? 'Payments to vendors' : v.type === 'EXP' ? 'Direct expenses paid' : v.type === 'OB' ? 'Opening capital' : acct?.name ?? v.narration;
    buckets[bucket].set(label, r2((buckets[bucket].get(label) ?? 0) + bankMove));
  }
  const opening = r2(s.vouchers.filter((v) => v.date < from).flatMap((v) => v.lines).filter((l) => bankCodes.includes(l.account)).reduce((t, l) => t + l.debit - l.credit, 0));
  return { buckets, opening };
}
