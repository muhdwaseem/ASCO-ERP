import { describe, expect, it } from 'vitest';
import { seed } from './seed';
import {
  ACC, aging, balanceSheet, balances, invoiceOutstanding, naturalBalance, pnl, postJournal, postReceipt, postSalesInvoice,
  PostingError, r2, trialBalance,
} from './ledger';

const U = 'test';
const AS_OF = '2026-09-30';

describe('posting engine', () => {
  it('seeded books are in balance', () => {
    const s = seed();
    const tb = trialBalance(s);
    const dr = r2(tb.reduce((a, r) => a + r.debit, 0));
    const cr = r2(tb.reduce((a, r) => a + r.credit, 0));
    expect(dr).toBeGreaterThan(0);
    expect(dr).toBe(cr);
  });

  it('balance sheet balances (assets = liabilities + equity incl. current profit)', () => {
    const s = seed();
    const b = balanceSheet(s, AS_OF);
    expect(b.totalAssets).toBe(r2(b.totalLiab + b.totalEquity));
  });

  it('AR subledger (aging) reconciles to the AR control account', () => {
    const s = seed();
    const bal = balances(s, { to: AS_OF });
    const control = naturalBalance(s, ACC.AR, bal.get(ACC.AR)!);
    const sub = r2(aging(s, 'AR', AS_OF).reduce((a, r) => a + r.total, 0));
    expect(sub).toBe(control);
  });

  it('AP subledger reconciles to the AP control account', () => {
    const s = seed();
    const bal = balances(s, { to: AS_OF });
    const control = naturalBalance(s, ACC.AP, bal.get(ACC.AP)!);
    const sub = r2(aging(s, 'AP', AS_OF).reduce((a, r) => a + r.total, 0));
    expect(sub).toBe(control);
  });

  it('rejects an out-of-balance voucher and writes nothing', () => {
    const s = seed();
    const n = s.vouchers.length;
    expect(() => postJournal(s, '2026-09-10', 'bad', [
      { account: ACC.BANK, debit: 100, credit: 0 }, { account: ACC.MISC, debit: 0, credit: 90 },
    ], U)).toThrow(PostingError);
    expect(s.vouchers.length).toBe(n);
  });

  it('rejects posting into a closed period', () => {
    const s = seed();
    expect(() => postJournal(s, '2026-03-15', 'late', [
      { account: ACC.MISC, debit: 50, credit: 0 }, { account: ACC.BANK, debit: 0, credit: 50 },
    ], U)).toThrow(/closed/);
  });

  it('rejects posting to a header account', () => {
    const s = seed();
    expect(() => postJournal(s, '2026-09-10', 'hdr', [
      { account: '5300', debit: 50, credit: 0 }, { account: ACC.BANK, debit: 0, credit: 50 },
    ], U)).toThrow(/header/);
  });

  it('invoice + voucher share one number; VAT at 5%', () => {
    const s = seed();
    const no = postSalesInvoice(s, { party: 'C-0001', date: '2026-09-20', dueDate: '2026-10-20', lines: [{ description: 'x', account: ACC.REV, quantity: 2, unitPrice: 1000, vatRate: 0.05 }] }, U);
    const v = s.vouchers.find((x) => x.voucherNo === no)!;
    expect(v).toBeTruthy();
    expect(v.lines.find((l) => l.account === ACC.VAT_OUT)!.credit).toBe(100);
    expect(v.lines.find((l) => l.account === ACC.AR)!.debit).toBe(2100);
    expect(invoiceOutstanding(s, no)).toBe(2100);
  });

  it('receipt cannot exceed invoice outstanding', () => {
    const s = seed();
    const no = postSalesInvoice(s, { party: 'C-0002', date: '2026-09-20', dueDate: '2026-10-20', lines: [{ description: 'x', account: ACC.REV, quantity: 1, unitPrice: 1000, vatRate: 0.05 }] }, U);
    expect(() => postReceipt(s, { party: 'C-0002', againstDoc: no, date: '2026-09-21', bankAccount: ACC.BANK, amount: 1100, paymentMode: 'Cash' }, U)).toThrow(/exceeds/);
    postReceipt(s, { party: 'C-0002', againstDoc: no, date: '2026-09-21', bankAccount: ACC.BANK, amount: 1050, paymentMode: 'Cash' }, U);
    expect(invoiceOutstanding(s, no)).toBe(0);
  });

  it('P&L net profit equals the equity movement from profit', () => {
    const s = seed();
    const p = pnl(s, '2026-01-01', AS_OF);
    expect(p.net).toBe(balanceSheet(s, AS_OF).profit);
    expect(p.totalRev).toBeGreaterThan(p.totalExp * 0); // sanity: revenue exists
  });
});
