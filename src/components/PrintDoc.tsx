// Printable tax invoice / receipt / payslips from live data. Rendered into document.body
// (outside the workbook) so window.print() outputs only the document.
import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import { useQuery } from '@tanstack/react-query';
import { X, Printer } from 'lucide-react';
import { api } from '../api/client';

export type PrintKind = 'invoice' | 'receipt' | 'payslips' | 'quotation';
type J = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

const f = (n?: number | null) => (n ?? 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const PATH: Record<PrintKind, string> = { invoice: '/sales-invoices/', receipt: '/receipts/', payslips: '/payroll-runs/', quotation: '/estimates/' };

export function PrintDoc({ kind, id, companyId, onClose }: { kind: PrintKind; id: number; companyId: number; onClose: () => void }) {
  const company = useQuery({ queryKey: ['company-profile', companyId], queryFn: () => api.get<J>('/company-profile', companyId) });
  const doc = useQuery({ queryKey: ['print', kind, id, companyId], queryFn: () => api.get<J>(`${PATH[kind]}${id}`, companyId) });

  useEffect(() => {
    document.body.classList.add('printing-doc');
    return () => document.body.classList.remove('printing-doc');
  }, []);

  const c = company.data;
  const d = doc.data;
  const error = company.error ?? doc.error;

  return createPortal(
    <div className="print-shell">
      <div className="print-bar">
        <span>Print preview</span>
        <button className="btn primary" disabled={!c || !d} onClick={() => window.print()}><Printer size={14} /> Print</button>
        <button className="icon-btn" onClick={onClose} aria-label="Close preview"><X size={18} /></button>
      </div>
      {error ? <div className="print-page"><p>{error.message}</p></div> : !c || !d ? <div className="print-page"><p>Loading…</p></div> : (
        <div className="print-page">
          <header className="pd-head">
            <div>
              <h1>{c.tradeName || c.legalName}</h1>
              <p>{[c.city, c.addressEmirate, c.poBox && `P.O. Box ${c.poBox}`, c.addressCountry].filter(Boolean).join(', ')}</p>
              {c.phone && <p>Tel {c.phone}</p>}
              {c.trnNumber && <p>TRN {c.trnNumber}</p>}
            </div>
            <div className="pd-title">{kind === 'invoice' ? (c.vatRegistered ? 'TAX INVOICE' : 'INVOICE') : kind === 'quotation' ? 'QUOTATION' : kind === 'receipt' ? 'RECEIPT VOUCHER' : 'PAYSLIPS'}</div>
          </header>

          {kind === 'invoice' && (
            <>
              <section className="pd-meta">
                <div><b>Bill to</b><p>{d.customer.name}</p>{d.customer.address && <p>{d.customer.address}</p>}{d.customer.trn && <p>TRN {d.customer.trn}</p>}</div>
                <dl><dt>Invoice no.</dt><dd>{d.invoiceNo}</dd><dt>Date</dt><dd>{d.date}</dd><dt>Due date</dt><dd>{d.dueDate}</dd>{d.customerPoNo && <><dt>PO no.</dt><dd>{d.customerPoNo}</dd></>}</dl>
              </section>
              <table className="pd-lines">
                <thead><tr><th>#</th><th>Description</th><th>Qty</th><th>Rate</th><th>Net</th><th>VAT %</th><th>VAT</th><th>Total</th></tr></thead>
                <tbody>{d.lines.map((l: J) => <tr key={l.lineNo}><td>{l.lineNo}</td><td>{l.description}</td><td className="n">{l.quantity}</td><td className="n">{f(l.unitPrice)}</td><td className="n">{f(l.net)}</td><td className="n">{Math.round(l.vatRate * 100)}%</td><td className="n">{f(l.vat)}</td><td className="n">{f(l.gross)}</td></tr>)}</tbody>
              </table>
              <section className="pd-totals"><div><span>Net</span><b>{f(d.net)}</b></div><div><span>VAT</span><b>{f(d.vat)}</b></div><div className="grand"><span>Total ({c.baseCurrency})</span><b>{f(d.gross)}</b></div>{d.balance != null && <div><span>Balance due</span><b>{f(d.balance)}</b></div>}</section>
              {c.bank && <section className="pd-note"><b>Bank details</b><p>{c.bank.bankName} · {c.bank.accountName} · IBAN {c.bank.iban}{c.bank.swift ? ` · SWIFT ${c.bank.swift}` : ''}</p></section>}
              {(d.termsAndConditions || c.invoiceDefaultTermsAndConditions) && <section className="pd-note"><b>Terms</b><p>{d.termsAndConditions || c.invoiceDefaultTermsAndConditions}</p></section>}
            </>
          )}

          {kind === 'quotation' && (
            <>
              <section className="pd-meta">
                <div><b>Prepared for</b><p>{d.customer.name}</p>{d.customer.address && <p>{d.customer.address}</p>}{d.customer.trn && <p>TRN {d.customer.trn}</p>}</div>
                <dl><dt>Quotation no.</dt><dd>{d.estimateNo}</dd><dt>Date</dt><dd>{d.date}</dd><dt>Valid until</dt><dd>{d.validUntil}</dd></dl>
              </section>
              <table className="pd-lines">
                <thead><tr><th>#</th><th>Description</th><th>Qty</th><th>Rate</th><th>Net</th><th>VAT %</th><th>VAT</th><th>Total</th></tr></thead>
                <tbody>{d.lines.map((l: J) => <tr key={l.lineNo}><td>{l.lineNo}</td><td>{l.description}</td><td className="n">{l.quantity}</td><td className="n">{f(l.unitPrice)}</td><td className="n">{f(l.net)}</td><td className="n">{Math.round(l.vatRate * 100)}%</td><td className="n">{f(l.vat)}</td><td className="n">{f(l.gross)}</td></tr>)}</tbody>
              </table>
              <section className="pd-totals"><div><span>Net</span><b>{f(d.net)}</b></div><div><span>VAT</span><b>{f(d.vat)}</b></div><div className="grand"><span>Total ({c.baseCurrency})</span><b>{f(d.gross)}</b></div></section>
              {d.narration && <section className="pd-note"><p>{d.narration}</p></section>}
              {c.invoiceDefaultTermsAndConditions && <section className="pd-note"><b>Terms</b><p>{c.invoiceDefaultTermsAndConditions}</p></section>}
              <section className="pd-sign"><span>For {c.tradeName || c.legalName}</span><span>Accepted by customer</span></section>
            </>
          )}

          {kind === 'receipt' && (
            <>
              <section className="pd-meta">
                <div><b>Received from</b><p>{d.customer.name}</p>{d.customer.trn && <p>TRN {d.customer.trn}</p>}</div>
                <dl><dt>Receipt no.</dt><dd>{d.receiptNo}</dd><dt>Date</dt><dd>{d.date}</dd><dt>Mode</dt><dd>{d.paymentMode}</dd>{d.referenceNo && <><dt>Reference</dt><dd>{d.referenceNo}</dd></>}</dl>
              </section>
              <section className="pd-totals"><div className="grand"><span>Amount received ({c.baseCurrency})</span><b>{f(d.amount)}</b></div>{d.invoiceNo && <div><span>Against invoice</span><b>{d.invoiceNo}</b></div>}<div><span>Deposited to</span><b>{d.bankAccount}</b></div></section>
              {d.narration && <section className="pd-note"><p>{d.narration}</p></section>}
              <section className="pd-sign"><span>Received by</span><span>Customer signature</span></section>
            </>
          )}

          {kind === 'payslips' && d.lines.map((l: J) => (
            <section key={l.employeeCode} className="pd-slip">
              <h2>{l.fullName} <small>{l.employeeCode} · {l.designation ?? ''} · {d.period}</small></h2>
              <table className="pd-lines">
                <tbody>
                  <tr><td>Basic salary</td><td className="n">{f(l.basicSalary)}</td><td>Deductions</td><td className="n">{f(l.deductions)}</td></tr>
                  <tr><td>Housing allowance</td><td className="n">{f(l.housingAllowance)}</td><td>Salary advance recovery</td><td className="n">{f(l.salaryAdvanceDeduction)}</td></tr>
                  <tr><td>Transport allowance</td><td className="n">{f(l.transportAllowance)}</td><td /><td /></tr>
                  <tr><td>Other allowance</td><td className="n">{f(l.otherAllowance)}</td><td /><td /></tr>
                  <tr className="tot"><td>Gross pay</td><td className="n">{f(l.grossPay)}</td><td>Net pay</td><td className="n">{f(l.netPay)}</td></tr>
                </tbody>
              </table>
              {l.iban && <p className="pd-small">Paid to IBAN {l.iban}</p>}
            </section>
          ))}
        </div>
      )}
    </div>,
    document.body,
  );
}
