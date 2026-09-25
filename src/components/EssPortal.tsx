// Employee Self-Service portal (employee logins): own profile, payslips, leave, salary advances.
import { useState, type FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { LogOut } from 'lucide-react';
import { ApiError, sessionActions, useSession } from '../api/client';

type J = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
const f = (n?: number | null) => (n ?? 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

async function ess<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api/ess${path}`, {
    method: body ? 'POST' : 'GET', credentials: 'same-origin',
    headers: { 'X-ASCO': '1', ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  const json = text ? JSON.parse(text) : undefined;
  if (!res.ok) throw new ApiError(res.status, json?.detail ?? `Request failed (${res.status})`);
  return json as T;
}

export function EssPortal() {
  const s = useSession();
  const qc = useQueryClient();
  const [tab, setTab] = useState<'profile' | 'payslips' | 'leave' | 'advances'>('payslips');
  const profile = useQuery({ queryKey: ['ess', 'profile'], queryFn: () => ess<J>('/profile') });
  const payslips = useQuery({ queryKey: ['ess', 'payslips'], queryFn: () => ess<J[]>('/payslips'), enabled: tab === 'payslips' });
  const leave = useQuery({ queryKey: ['ess', 'leave'], queryFn: () => ess<J>('/leave'), enabled: tab === 'leave' });
  const advances = useQuery({ queryKey: ['ess', 'advances'], queryFn: () => ess<J[]>('/advances'), enabled: tab === 'advances' });
  const [form, setForm] = useState({ type: 'Annual', startDate: '', endDate: '', reason: '' });
  const [msg, setMsg] = useState<{ text: string; err?: boolean } | null>(null);

  const request = async (e: FormEvent) => {
    e.preventDefault();
    setMsg(null);
    try {
      const r = await ess<J>('/leave', form);
      setMsg({ text: `Leave requested — ${r.days} day(s), ${r.status}` });
      setForm({ type: 'Annual', startDate: '', endDate: '', reason: '' });
      qc.invalidateQueries({ queryKey: ['ess', 'leave'] });
    } catch (err) {
      setMsg({ text: err instanceof Error ? err.message : String(err), err: true });
    }
  };

  const p = profile.data;
  return (
    <div className="ess">
      <header className="titlebar">
        <div className="tb-left"><div className="logo">A</div></div>
        <div className="tb-title">Employee Portal - ASCO</div>
        <div className="tb-right"><span className="tb-user">{s.me?.employee?.fullName}</span><button className="tb-signout" onClick={() => sessionActions.signOut()} aria-label="Sign out"><LogOut size={14} /></button></div>
      </header>
      <nav className="menubar">
        {(['payslips', 'leave', 'advances', 'profile'] as const).map((t) => (
          <button key={t} className={t === tab ? 'mb-tab on' : 'mb-tab'} onClick={() => setTab(t)}>{t[0].toUpperCase() + t.slice(1)}</button>
        ))}
      </nav>
      <main className="ess-body">
        {tab === 'profile' && p && (
          <dl className="ess-dl">
            <dt>Employee</dt><dd>{p.fullName} ({p.employeeCode})</dd>
            <dt>Designation</dt><dd>{p.designation ?? '—'}</dd>
            <dt>Joined</dt><dd>{p.joiningDate}</dd>
            <dt>Gross salary</dt><dd>{f(p.grossSalary)} (basic {f(p.basicSalary)})</dd>
            <dt>Bank</dt><dd>{p.bankName ?? '—'} {p.iban ? `· ${p.iban}` : ''}</dd>
            <dt>Visa expiry</dt><dd>{p.visaExpiryDate ?? '—'}</dd>
            <dt>Emirates ID expiry</dt><dd>{p.emiratesIdExpiryDate ?? '—'}</dd>
            <dt>Passport expiry</dt><dd>{p.passportExpiryDate ?? '—'}</dd>
          </dl>
        )}
        {tab === 'payslips' && (
          <table className="ess-table">
            <thead><tr><th>Period</th><th>Run date</th><th>Basic</th><th>Allowances</th><th>Deductions</th><th>Net pay</th><th>Paid</th></tr></thead>
            <tbody>
              {(payslips.data ?? []).map((l) => (
                <tr key={l.runId}><td>{l.period}</td><td>{l.runDate}</td><td className="n">{f(l.basicSalary)}</td><td className="n">{f(l.housingAllowance + l.transportAllowance + l.otherAllowance)}</td><td className="n">{f(l.deductions + l.salaryAdvanceDeduction)}</td><td className="n"><b>{f(l.netPay)}</b></td><td>{l.isPaid ? 'Yes' : 'Pending'}</td></tr>
              ))}
              {payslips.data?.length === 0 && <tr><td colSpan={7} className="muted">No posted payslips yet.</td></tr>}
            </tbody>
          </table>
        )}
        {tab === 'leave' && (
          <>
            {leave.data && <p className="ess-balance">Annual leave: <b>{leave.data.balance.remainingDays}</b> days remaining ({leave.data.balance.accruedDays} accrued, {leave.data.balance.usedDays} used)</p>}
            <form className="ess-form" onSubmit={request}>
              <select value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value })}>{['Annual', 'Sick', 'Unpaid', 'Other'].map((t) => <option key={t}>{t}</option>)}</select>
              <input type="date" required value={form.startDate} onChange={(e) => setForm({ ...form, startDate: e.target.value })} aria-label="From" />
              <input type="date" required value={form.endDate} onChange={(e) => setForm({ ...form, endDate: e.target.value })} aria-label="To" />
              <input placeholder="Reason" value={form.reason} onChange={(e) => setForm({ ...form, reason: e.target.value })} />
              <button className="btn primary" type="submit">Request leave</button>
            </form>
            {msg && <div className={msg.err ? 'form-error' : 'ess-ok'}>{msg.text}</div>}
            <table className="ess-table">
              <thead><tr><th>Type</th><th>From</th><th>To</th><th>Days</th><th>Reason</th><th>Status</th><th>Decided by</th></tr></thead>
              <tbody>{(leave.data?.requests ?? []).map((r: J) => <tr key={r.id}><td>{r.type}</td><td>{r.startDate}</td><td>{r.endDate}</td><td className="n">{r.days}</td><td>{r.reason}</td><td>{r.status}</td><td>{r.decisionBy ?? ''}</td></tr>)}</tbody>
            </table>
          </>
        )}
        {tab === 'advances' && (
          <table className="ess-table">
            <thead><tr><th>Issued</th><th>Amount</th><th>Monthly deduction</th><th>Remaining</th><th>Status</th></tr></thead>
            <tbody>
              {(advances.data ?? []).map((a) => <tr key={a.id}><td>{a.issueDate}</td><td className="n">{f(a.amount)}</td><td className="n">{f(a.monthlyDeductionAmount)}</td><td className="n">{f(a.remainingBalance)}</td><td>{a.status}</td></tr>)}
              {advances.data?.length === 0 && <tr><td colSpan={5} className="muted">No salary advances.</td></tr>}
            </tbody>
          </table>
        )}
      </main>
    </div>
  );
}
