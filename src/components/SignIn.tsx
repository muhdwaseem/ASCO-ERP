// Excel-style start screen: sign in against the ASCO API (same users as C-ERP), pick a company,
// or explore offline with in-browser demo books.
import { useState, type FormEvent } from 'react';
import { Building2, ArrowRight } from 'lucide-react';
import { sessionActions, useSession } from '../api/client';

export function SignIn() {
  const s = useSession();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await sessionActions.signIn(email, password);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const choosing = s.mode === 'live' && s.me && !s.companyId;

  return (
    <div className="start">
      <aside>
        <div className="start-logo">A</div>
        <h1>ASCO</h1>
        <p>Accounting · Payroll · CRM · Reports</p>
      </aside>
      <main>
        {choosing ? (
          <>
            <h2>Choose a company</h2>
            <p className="muted">Signed in as {s.me!.displayName}. You have access to {s.me!.companies.length} companies.</p>
            {s.me!.companies.length === 0 && <div className="form-error">No company has been assigned to your account yet — ask your firm administrator.</div>}
            <div className="company-list">
              {s.me!.companies.map((c) => (
                <button key={c.id} onClick={() => sessionActions.switchCompany(c.id)} disabled={c.subscription === 'Suspended' && !s.me!.isFirmAdmin}>
                  <Building2 size={20} />
                  <span><b>{c.name}</b><em>{c.code} · {c.role}{c.subscription === 'Suspended' ? ' · subscription suspended' : ''}</em></span>
                  <ArrowRight size={16} />
                </button>
              ))}
            </div>
            <button className="linkish" onClick={() => sessionActions.signOut()}>Sign out</button>
          </>
        ) : (
          <>
            <h2>Sign in</h2>
            <p className="muted">Use your company account (the same login as C-ERP).</p>
            <form onSubmit={submit} className="start-form">
              <label>Email <input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required autoFocus /></label>
              <label>Password <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required /></label>
              {error && <div className="form-error">{error}</div>}
              <button className="btn primary" type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
            </form>
            <div className="start-demo">
              <span>No server handy?</span>
              <button className="linkish" onClick={() => sessionActions.startDemo()}>Explore with demo data (offline) →</button>
            </div>
          </>
        )}
      </main>
    </div>
  );
}
