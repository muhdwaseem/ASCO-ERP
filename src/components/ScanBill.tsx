// Scan Bill: upload a vendor bill → C-ERP's BillScanningService extracts it → review → the purchase
// invoice form opens pre-filled. Nothing posts until the user saves that form.
import { useState } from 'react';
import { ScanLine, Upload } from 'lucide-react';
import type { LivePrefill } from './LiveDocForm';

type J = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

export function ScanBill({ companyId, onUse }: { companyId: number; onUse: (prefill: LivePrefill) => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<J | null>(null);

  const upload = async (file: File) => {
    setBusy(true); setError(''); setResult(null);
    try {
      const form = new FormData();
      form.append('file', file);
      const res = await fetch('/api/ai/scan-bill', { method: 'POST', body: form, credentials: 'same-origin', headers: { 'X-ASCO': '1', 'X-Company-Id': String(companyId) } });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(body.detail ?? `Scan failed (${res.status})`);
      setResult(body);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const bill = result?.extracted;
  return (
    <div className="ai-panel">
      <div className="ai-head"><ScanLine size={18} /> Scan a vendor bill <span className="pill">extracts → you review → then post</span></div>
      <label className="scan-drop">
        <Upload size={22} />
        <span>{busy ? 'Reading the bill…' : 'Choose a PDF or photo of the bill (max 10 MB)'}</span>
        <input type="file" accept="application/pdf,image/*" disabled={busy} onChange={(e) => e.target.files?.[0] && upload(e.target.files[0])} />
      </label>
      {error && <div className="form-error">{error}</div>}
      {bill && (
        <div className="scan-result">
          <dl className="ess-dl">
            <dt>Vendor</dt><dd>{bill.vendorName ?? '—'} {result?.vendor ? `→ matched ${result.vendor.code} ${result.vendor.name}` : '(no match)'}</dd>
            <dt>TRN</dt><dd>{bill.vendorTrn ?? '—'}</dd>
            <dt>Bill no.</dt><dd>{bill.invoiceNumber ?? '—'}</dd>
            <dt>Date / due</dt><dd>{bill.date ?? '—'} / {bill.dueDate ?? '—'}</dd>
            <dt>Total</dt><dd>{bill.total ?? '—'}</dd>
          </dl>
          <table className="ess-table">
            <thead><tr><th>Description</th><th>Qty</th><th>Unit price</th><th>VAT</th></tr></thead>
            <tbody>{(bill.lines ?? []).map((l: J, i: number) => <tr key={i}><td>{l.description}</td><td className="n">{l.quantity}</td><td className="n">{l.unitPrice}</td><td className="n">{Math.round((l.vatRate > 1 ? l.vatRate / 100 : l.vatRate) * 100)}%</td></tr>)}</tbody>
          </table>
          {result?.note && <p className="muted">{result.note}</p>}
          <button className="btn primary" onClick={() => onUse({
            partyId: result?.vendor?.id, date: bill.date ?? undefined, reference: bill.invoiceNumber ?? undefined,
            lines: (bill.lines ?? []).map((l: J) => ({ description: l.description, qty: String(l.quantity), price: String(l.unitPrice), vat: String(l.vatRate > 1 ? l.vatRate / 100 : l.vatRate) })),
          })}>Create purchase invoice from this bill</button>
        </div>
      )}
    </div>
  );
}
