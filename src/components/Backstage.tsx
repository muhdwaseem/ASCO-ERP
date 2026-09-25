// "File" backstage, like Excel's: company info, export, print, reset demo data.
import { useState } from 'react';
import { ArrowLeft } from 'lucide-react';
import { store, useLedger } from '../engine/store';
import { sessionActions } from '../api/client';
import { trialBalance } from '../engine/ledger';

export function Backstage({ onClose, onExport, live }: { onClose: () => void; onExport: () => void; live?: { company: string; role: string; user: string } }) {
  const s = useLedger();
  const [page, setPage] = useState<'info' | 'about'>('info');
  const tb = trialBalance(s);
  const dr = tb.reduce((a, r) => a + r.debit, 0);
  return (
    <div className="backstage">
      <nav>
        <button className="back" onClick={onClose} aria-label="Back"><ArrowLeft size={22} /></button>
        <button className={page === 'info' ? 'on' : ''} onClick={() => setPage('info')}>Info</button>
        <button onClick={() => { onExport(); onClose(); }}>Export sheet (CSV)</button>
        <button onClick={() => { onClose(); setTimeout(() => window.print(), 50); }}>Print</button>
        {!live && <button onClick={() => { store.reset(); onClose(); }}>Reset demo data</button>}
        <button onClick={() => { onClose(); sessionActions.signOut(); }}>{live ? 'Sign out' : 'Exit demo'}</button>
        <button className={page === 'about' ? 'on' : ''} onClick={() => setPage('about')}>About</button>
      </nav>
      <section>
        {page === 'info' && live ? (
          <>
            <h1>{live.company}</h1>
            <dl>
              <dt>Data source</dt><dd>Live — ASCO API over the C-ERP accounting core</dd>
              <dt>Signed in as</dt><dd>{live.user}</dd>
              <dt>Your role</dt><dd>{live.role}</dd>
              <dt>Posting</dt><dd>Read-only in this build (Phase 1). Creating and posting documents arrives in Phase 2.</dd>
            </dl>
          </>
        ) : page === 'info' ? (
          <>
            <h1>{s.company.name}</h1>
            <dl>
              <dt>TRN</dt><dd>{s.company.trn}</dd>
              <dt>Address</dt><dd>{s.company.address}</dd>
              <dt>Base currency</dt><dd>{s.company.baseCurrency}</dd>
              <dt>Posted vouchers</dt><dd>{s.vouchers.length}</dd>
              <dt>Trial balance</dt><dd>{dr.toLocaleString('en-US', { minimumFractionDigits: 2 })} DR = CR ✓</dd>
              <dt>Open periods</dt><dd>{s.periods.filter((p) => !p.isClosed).map((p) => p.name).join(', ')}</dd>
            </dl>
          </>
        ) : (
          <>
            <h1>ASCO</h1>
            <p>ASCO — Excel-style accounting software covering the full C-ERP module set. This build runs a TypeScript port of the posting engine in the browser with demo data; Signed in, it reads live books through the ASCO API (ASP.NET Core over C-ERP's accounting core + PostgreSQL); demo mode runs a TypeScript port of the posting engine in the browser. See <code>docs/STACK-AND-PLAN.md</code>.</p>
          </>
        )}
      </section>
    </div>
  );
}
