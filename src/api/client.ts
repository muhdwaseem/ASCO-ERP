// Thin client for the ASCO API (server/Asco.Api). Cookie auth, same origin.
import { useSyncExternalStore } from 'react';

export interface CompanyGrant {
  id: number; code: string; name: string; role: string;
  canPost: boolean; canAdminister: boolean; canAccessPayroll: boolean; subscription: string;
}
export interface Me { email: string; displayName: string; isFirmAdmin: boolean; companies: CompanyGrant[] }

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}

async function call<T>(path: string, init: RequestInit = {}, companyId?: number): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set('X-ASCO', '1');
  if (companyId !== undefined) headers.set('X-Company-Id', String(companyId));
  if (init.body) headers.set('Content-Type', 'application/json');
  let res: Response;
  try {
    res = await fetch(path, { ...init, headers, credentials: 'same-origin' });
  } catch {
    throw new ApiError(0, 'Cannot reach the ASCO API — is it running? (dotnet run --project server/Asco.Api)');
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  const body = text ? safeJson(text) : undefined;
  if (!res.ok) {
    const detail = (body as { detail?: string; error?: string } | undefined)?.detail ?? (body as { error?: string } | undefined)?.error;
    const fallback = res.status === 502 || res.status === 504 ? 'The ASCO API is not running (proxy could not connect).' : `Request failed (${res.status})`;
    throw new ApiError(res.status, detail ?? fallback);
  }
  return body as T;
}

const safeJson = (t: string) => { try { return JSON.parse(t); } catch { return undefined; } };

export const api = {
  login: (email: string, password: string) => call<void>('/api/auth/login', { method: 'POST', body: JSON.stringify({ email, password }) }),
  logout: () => call<void>('/api/auth/logout', { method: 'POST' }),
  me: () => call<Me>('/api/auth/me'),
  get: <T>(path: string, companyId: number) => call<T>(`/api${path}`, {}, companyId),
  post: <T>(path: string, body: unknown, companyId: number) => call<T>(`/api${path}`, { method: 'POST', body: JSON.stringify(body ?? {}) }, companyId),
  /** Downloads a file endpoint (e.g. the WPS SIF) with the company header attached. */
  async download(path: string, companyId: number) {
    const res = await fetch(`/api${path}`, { headers: { 'X-ASCO': '1', 'X-Company-Id': String(companyId) }, credentials: 'same-origin' });
    if (!res.ok) {
      const body = safeJson(await res.text()) as { detail?: string } | undefined;
      throw new ApiError(res.status, body?.detail ?? `Download failed (${res.status})`);
    }
    const name = /filename="?([^";]+)"?/.exec(res.headers.get('Content-Disposition') ?? '')?.[1] ?? 'download.txt';
    const a = document.createElement('a');
    a.href = URL.createObjectURL(await res.blob());
    a.download = name;
    a.click();
    URL.revokeObjectURL(a.href);
    return name;
  },
};

export interface Lookups {
  accounts: { id: number; code: string; name: string; type: string; category?: string }[];
  bankAccounts: { id: number; code: string; name: string; balance: number }[];
  costCenters: { id: number; code: string; name: string }[];
  customers: { id: number; code: string; name: string; paymentTermsDays: number }[];
  vendors: { id: number; code: string; name: string }[];
  items: { id: number; code: string; name: string; kind: string; unit: string; sellingPrice: number; costPrice?: number; salesAccountId?: number; purchaseAccountId?: number; vatRate?: number | null }[];
  taxCodes: { id: number; code: string; description: string; rate: number }[];
  openPeriods: { id: number; name: string; startDate: string; endDate: string }[];
}
/** C-ERP stores VAT as a fraction (0.05) on documents; tax codes may hold 5. Normalise to a fraction. */
export const vatFraction = (r?: number | null) => (r == null ? 0.05 : r > 1 ? r / 100 : r);

// ---------------------------------------------------------------- session store

export type Mode = 'demo' | 'live';
interface Session { mode: Mode | null; me: Me | null; companyId: number | null }

const read = (k: string) => { try { return localStorage.getItem(k); } catch { return null; } };
const write = (k: string, v: string | null) => { try { if (v === null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch { /* storage blocked */ } };

let session: Session = { mode: read('asco.mode') === 'demo' ? 'demo' : null, me: null, companyId: Number(read('asco.company')) || null };
let ver = 0;
const subs = new Set<() => void>();
const set = (s: Partial<Session>) => {
  session = { ...session, ...s };
  write('asco.mode', session.mode === 'demo' ? 'demo' : null);
  if (session.companyId) write('asco.company', String(session.companyId));
  ver++;
  subs.forEach((f) => f());
};

export function useSession() {
  useSyncExternalStore((cb) => { subs.add(cb); return () => subs.delete(cb); }, () => ver);
  return session;
}

export const sessionActions = {
  startDemo: () => set({ mode: 'demo', me: null }),
  async signIn(email: string, password: string) {
    await api.login(email, password);
    await sessionActions.restore();
  },
  /** Re-attach to an existing cookie session (page reload). Returns false when not signed in. */
  async restore() {
    try {
      const me = await api.me();
      const keep = me.companies.find((c) => c.id === session.companyId);
      set({ mode: 'live', me, companyId: keep?.id ?? (me.companies.length === 1 ? me.companies[0].id : null) });
      return true;
    } catch {
      return false;
    }
  },
  switchCompany: (id: number) => set({ companyId: id }),
  async signOut() {
    if (session.mode === 'live') await api.logout().catch(() => {});
    set({ mode: null, me: null });
  },
};
