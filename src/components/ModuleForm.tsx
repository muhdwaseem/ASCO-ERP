// One spec-driven dialog for every industry-module entry (inventory, manufacturing, jobs, fleet,
// industry setup). Dropdown lists load from the live API; saving posts through the ASCO API.
import { useEffect, useMemo, useState } from 'react';
import { X } from 'lucide-react';
import { useQueries, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';

type J = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
type Source = 'accounts' | 'banks' | 'customers' | 'items' | 'warehouses' | 'boms' | 'jobs' | 'vehicles' | 'invoices' | 'profile';
const SOURCES: Record<Source, { path: string; pick: (d: any) => { value: string; label: string }[] }> = { // eslint-disable-line @typescript-eslint/no-explicit-any
  accounts: { path: '/lookups', pick: (d) => d.accounts.map((a: J) => ({ value: String(a.id), label: `${a.code} · ${a.name} (${a.type})` })) },
  banks: { path: '/lookups', pick: (d) => d.bankAccounts.map((a: J) => ({ value: String(a.id), label: `${a.code} · ${a.name}` })) },
  customers: { path: '/lookups', pick: (d) => d.customers.map((c: J) => ({ value: String(c.id), label: `${c.code} · ${c.name}` })) },
  items: { path: '/inventory/stockable-items', pick: (d) => d.map((i: J) => ({ value: String(i.id), label: `${i.code} · ${i.name}` })) },
  warehouses: { path: '/inventory/warehouses', pick: (d) => d.map((w: J) => ({ value: String(w.id), label: `${w.code} · ${w.name}` })) },
  boms: { path: '/manufacturing/boms', pick: (d) => d.map((b: J) => ({ value: String(b.id), label: `${b.code} · ${b.name}` })) },
  jobs: { path: '/jobs', pick: (d) => d.map((j: J) => ({ value: String(j.id), label: `${j.jobNo} · ${j.title}` })) },
  vehicles: { path: '/fleet/vehicles', pick: (d) => d.map((v: J) => ({ value: String(v.id), label: `${v.plateNo} · ${v.type}` })) },
  invoices: { path: '/sales-invoices', pick: (d) => d.map((i: J) => ({ value: String(i.id), label: `${i.invoiceNo} · ${i.customerName} · ${i.gross}` })) },
  profile: { path: '/modules/profile', pick: () => [] },
};

export interface Field {
  key: string; label: string;
  type: 'text' | 'number' | 'date' | 'select' | 'checks';
  source?: Source; options?: { value: string; label: string }[];
  required?: boolean; optional?: string; // label for the empty option
  showIf?: (v: J) => boolean; full?: boolean;
}
export interface FormSpec {
  title: string;
  method?: 'POST' | 'PUT';
  path: (v: J) => string;
  fields: Field[];
  lines?: { label: string; cost?: 'required' | 'optional'; signed?: boolean };
  body?: (v: J, lines: J[]) => unknown;
  done: (res: J) => string;
  target?: string;
  initial?: (profile?: J) => J;
}

const today = () => new Date().toISOString().slice(0, 10);
const n = (v: unknown) => (v === '' || v == null ? null : Number(v));
const opt = (x: string[]) => x.map((v) => ({ value: v, label: v.replace(/([a-z])([A-Z])/g, '$1 $2') }));

export const MODULE_FORMS: Record<string, FormSpec> = {
  warehouse: {
    title: 'New Warehouse', path: () => '/inventory/warehouses', target: 'warehouses',
    fields: [{ key: 'code', label: 'Code', type: 'text', required: true }, { key: 'name', label: 'Name', type: 'text', required: true }],
    done: (r) => `Created warehouse ${r.code}`,
  },
  receipt: {
    title: 'Stock Receipt (GRN)', path: () => '/inventory/receipts', target: 'stock-moves',
    fields: [
      { key: 'date', label: 'Date', type: 'date', required: true }, { key: 'warehouseId', label: 'Into warehouse', type: 'select', source: 'warehouses', required: true },
      { key: 'creditAccountId', label: 'Credit account', type: 'select', source: 'accounts', optional: 'Default: goods-received clearing' },
      { key: 'reference', label: 'Supplier ref / GRN', type: 'text' }, { key: 'narration', label: 'Narration', type: 'text', full: true },
    ],
    lines: { label: 'Items received', cost: 'required' },
    body: (v, lines) => ({ date: v.date, warehouseId: n(v.warehouseId), creditAccountId: n(v.creditAccountId), reference: v.reference || null, narration: v.narration || null, lines }),
    done: (r) => `Received ${r.moveNo} — value ${r.value} posted as ${r.voucherNo}`,
  },
  issue: {
    title: 'Stock Issue', path: () => '/inventory/issues', target: 'stock-moves',
    fields: [
      { key: 'date', label: 'Date', type: 'date', required: true }, { key: 'warehouseId', label: 'From warehouse', type: 'select', source: 'warehouses', required: true },
      { key: 'debitAccountId', label: 'Charge to account', type: 'select', source: 'accounts', optional: 'Default: cost of goods sold' },
      { key: 'reference', label: 'Reference', type: 'text' }, { key: 'narration', label: 'Narration', type: 'text', full: true },
    ],
    lines: { label: 'Items issued' },
    body: (v, lines) => ({ date: v.date, warehouseId: n(v.warehouseId), debitAccountId: n(v.debitAccountId), reference: v.reference || null, narration: v.narration || null, lines }),
    done: (r) => `Issued ${r.moveNo} at average cost ${r.value} — ${r.voucherNo}`,
  },
  transfer: {
    title: 'Warehouse Transfer', path: () => '/inventory/transfers', target: 'stock-moves',
    fields: [
      { key: 'date', label: 'Date', type: 'date', required: true },
      { key: 'fromWarehouseId', label: 'From', type: 'select', source: 'warehouses', required: true }, { key: 'toWarehouseId', label: 'To', type: 'select', source: 'warehouses', required: true },
      { key: 'narration', label: 'Narration', type: 'text', full: true },
    ],
    lines: { label: 'Items moved' },
    body: (v, lines) => ({ date: v.date, fromWarehouseId: n(v.fromWarehouseId), toWarehouseId: n(v.toWarehouseId), narration: v.narration || null, lines }),
    done: (r) => `Transferred ${r.moveNo}`,
  },
  adjustment: {
    title: 'Stock Adjustment / Count', path: () => '/inventory/adjustments', target: 'stock-moves',
    fields: [
      { key: 'date', label: 'Date', type: 'date', required: true }, { key: 'warehouseId', label: 'Warehouse', type: 'select', source: 'warehouses', required: true },
      { key: 'reason', label: 'Reason', type: 'text', required: true, full: true },
    ],
    lines: { label: 'Quantity change (+ found / − missing)', cost: 'optional', signed: true },
    body: (v, lines) => ({ date: v.date, warehouseId: n(v.warehouseId), reason: v.reason, lines }),
    done: (r) => `Adjusted ${r.moveNo} (${r.voucherNo ?? 'no value change'})`,
  },
  'issue-invoice': {
    title: 'Relieve Stock for Sales Invoice', path: (v) => `/inventory/issue-for-invoice/${v.invoiceId}`, target: 'stock-moves',
    fields: [
      { key: 'invoiceId', label: 'Sales invoice', type: 'select', source: 'invoices', required: true, full: true },
      { key: 'warehouseId', label: 'Ship from warehouse', type: 'select', source: 'warehouses', required: true },
      { key: 'date', label: 'Date (default: invoice date)', type: 'date' },
    ],
    body: (v) => ({ warehouseId: n(v.warehouseId), date: v.date || null }),
    done: (r) => `${r.moveNo}: cost of goods sold ${r.value} posted as ${r.voucherNo}`,
  },
  reorder: {
    title: 'Set Reorder Level', method: 'PUT', path: (v) => `/inventory/items/${v.itemId}/reorder`, target: 'reorder',
    fields: [{ key: 'itemId', label: 'Item', type: 'select', source: 'items', required: true }, { key: 'reorderLevel', label: 'Reorder when total stock below', type: 'number', required: true }],
    body: (v) => ({ reorderLevel: n(v.reorderLevel) }),
    done: () => 'Reorder level saved',
  },
  bom: {
    title: 'New Bill of Materials', path: () => '/manufacturing/boms', target: 'boms',
    fields: [
      { key: 'code', label: 'BOM code', type: 'text', required: true }, { key: 'name', label: 'Name', type: 'text', required: true },
      { key: 'outputItemId', label: 'Produces item', type: 'select', source: 'items', required: true },
      { key: 'outputQuantity', label: 'Batch output quantity', type: 'number', required: true }, { key: 'conversionCost', label: 'Labour + overhead per batch', type: 'number' },
    ],
    lines: { label: 'Components per batch' },
    initial: () => ({ outputQuantity: '1', conversionCost: '0' }),
    body: (v, lines) => ({ code: v.code, name: v.name, outputItemId: n(v.outputItemId), outputQuantity: n(v.outputQuantity), conversionCost: n(v.conversionCost) ?? 0, lines: lines.map((l) => ({ componentItemId: l.itemId, quantity: l.quantity })) }),
    done: (r) => `Created BOM ${r.code}`,
  },
  'production-order': {
    title: 'New Production Order', path: () => '/manufacturing/orders', target: 'production-orders',
    fields: [
      { key: 'bomId', label: 'BOM', type: 'select', source: 'boms', required: true }, { key: 'quantity', label: 'Quantity to produce', type: 'number', required: true },
      { key: 'plannedDate', label: 'Planned date', type: 'date', required: true },
      { key: 'sourceWarehouseId', label: 'Components from', type: 'select', source: 'warehouses', required: true },
      { key: 'outputWarehouseId', label: 'Finished goods to', type: 'select', source: 'warehouses', required: true },
    ],
    body: (v) => ({ bomId: n(v.bomId), quantity: n(v.quantity), plannedDate: v.plannedDate, sourceWarehouseId: n(v.sourceWarehouseId), outputWarehouseId: n(v.outputWarehouseId) }),
    done: (r) => `Released ${r.orderNo}`,
  },
  job: {
    title: 'New Job', path: () => '/jobs', target: 'jobs',
    fields: [
      { key: 'title', label: 'Title', type: 'text', required: true, full: true },
      { key: 'type', label: 'Type', type: 'select', options: opt(['Shipment', 'Project', 'Service']), required: true },
      { key: 'customerId', label: 'Customer', type: 'select', source: 'customers', optional: '— none —' },
      { key: 'budget', label: 'Budget (cost)', type: 'number' }, { key: 'openedDate', label: 'Opened', type: 'date' },
      { key: 'mode', label: 'Mode', type: 'select', options: opt(['Air', 'Sea', 'Road', 'Courier', 'Rail']), optional: '—', showIf: (v) => v.type === 'Shipment' },
      { key: 'direction', label: 'Direction', type: 'select', options: opt(['Import', 'Export', 'Domestic']), optional: '—', showIf: (v) => v.type === 'Shipment' },
      { key: 'origin', label: 'Origin', type: 'text', showIf: (v) => v.type === 'Shipment' }, { key: 'destination', label: 'Destination', type: 'text', showIf: (v) => v.type === 'Shipment' },
      { key: 'carrier', label: 'Carrier / line', type: 'text', showIf: (v) => v.type === 'Shipment' }, { key: 'awbBl', label: 'AWB / BL no.', type: 'text', showIf: (v) => v.type === 'Shipment' },
      { key: 'containerNo', label: 'Container no.', type: 'text', showIf: (v) => v.type === 'Shipment' }, { key: 'packages', label: 'Packages', type: 'number', showIf: (v) => v.type === 'Shipment' },
      { key: 'weightKg', label: 'Weight (kg)', type: 'number', showIf: (v) => v.type === 'Shipment' },
      { key: 'etd', label: 'ETD', type: 'date', showIf: (v) => v.type === 'Shipment' }, { key: 'eta', label: 'ETA', type: 'date', showIf: (v) => v.type === 'Shipment' },
      { key: 'siteLocation', label: 'Site / location', type: 'text', showIf: (v) => v.type !== 'Shipment' }, { key: 'targetDate', label: 'Target completion', type: 'date', showIf: (v) => v.type !== 'Shipment' },
    ],
    initial: (p) => ({ type: p?.industry === 'Logistics' ? 'Shipment' : p?.industry === 'Construction' ? 'Project' : 'Service', budget: '0', openedDate: today() }),
    body: (v) => ({
      title: v.title, type: v.type, customerId: n(v.customerId), budget: n(v.budget) ?? 0, openedDate: v.openedDate || null,
      mode: v.mode || null, direction: v.direction || null, origin: v.origin || null, destination: v.destination || null, carrier: v.carrier || null, awbBl: v.awbBl || null,
      containerNo: v.containerNo || null, packages: n(v.packages), weightKg: n(v.weightKg), etd: v.etd || null, eta: v.eta || null, siteLocation: v.siteLocation || null, targetDate: v.targetDate || null,
    }),
    done: (r) => `Opened ${r.jobNo} — tag invoice/bill lines with cost centre ${r.costCenter} to track its P&L`,
  },
  'job-status': {
    title: 'Update Job Status', path: (v) => `/jobs/${v.jobId}/status`, target: 'jobs',
    fields: [{ key: 'jobId', label: 'Job', type: 'select', source: 'jobs', required: true, full: true }, { key: 'status', label: 'Status', type: 'select', options: opt(['Open', 'InProgress', 'Completed', 'Closed', 'Cancelled']), required: true }],
    body: (v) => ({ status: v.status }),
    done: () => 'Job status updated',
  },
  vehicle: {
    title: 'New Vehicle', path: () => '/fleet/vehicles', target: 'vehicles',
    fields: [{ key: 'plateNo', label: 'Plate no.', type: 'text', required: true }, { key: 'type', label: 'Type', type: 'select', options: opt(['Truck', 'Van', 'Pickup', 'Trailer', 'Car', 'Forklift']), required: true }, { key: 'capacityKg', label: 'Capacity (kg)', type: 'number' }],
    initial: () => ({ type: 'Truck' }),
    body: (v) => ({ plateNo: v.plateNo, type: v.type, capacityKg: n(v.capacityKg) }),
    done: (r) => `Added vehicle ${r.plateNo}`,
  },
  trip: {
    title: 'Log Trip', path: () => '/fleet/trips', target: 'trips',
    fields: [
      { key: 'vehicleId', label: 'Vehicle', type: 'select', source: 'vehicles', required: true }, { key: 'jobId', label: 'For job', type: 'select', source: 'jobs', optional: '— no job —' },
      { key: 'date', label: 'Date', type: 'date', required: true }, { key: 'driver', label: 'Driver', type: 'text' },
      { key: 'from', label: 'From', type: 'text', required: true }, { key: 'to', label: 'To', type: 'text', required: true },
      { key: 'distanceKm', label: 'Distance (km)', type: 'number' }, { key: 'fuelLitres', label: 'Fuel (L)', type: 'number' },
      { key: 'fuelCost', label: 'Fuel cost', type: 'number' }, { key: 'tolls', label: 'Tolls / Salik', type: 'number' }, { key: 'otherCost', label: 'Other cost', type: 'number' },
      { key: 'paidFromAccountId', label: 'Paid from', type: 'select', source: 'banks', optional: '— no costs —' },
    ],
    body: (v) => ({ vehicleId: n(v.vehicleId), jobId: n(v.jobId), driver: v.driver || null, date: v.date, from: v.from, to: v.to, distanceKm: n(v.distanceKm) ?? 0, fuelLitres: n(v.fuelLitres) ?? 0, fuelCost: n(v.fuelCost) ?? 0, tolls: n(v.tolls) ?? 0, otherCost: n(v.otherCost) ?? 0, paidFromAccountId: n(v.paidFromAccountId) }),
    done: (r) => `Logged ${r.tripNo}${r.voucherNo ? ` — costs posted as ${r.voucherNo}` : ''}`,
  },
  industry: {
    title: 'Configure Industry & Modules', method: 'PUT', path: () => '/modules/profile', target: 'industry-modules',
    fields: [
      { key: 'industry', label: 'Industry', type: 'select', required: true, full: true, options: [
        { value: 'General', label: 'General / Services firm' }, { value: 'Trading', label: 'Trading & Distribution' }, { value: 'Retail', label: 'Retail' },
        { value: 'Manufacturing', label: 'Manufacturing' }, { value: 'Logistics', label: 'Logistics & Freight' }, { value: 'Construction', label: 'Construction & Projects' }, { value: 'Services', label: 'Professional & PRO services' }] },
      { key: 'modules', label: 'Modules (leave all unticked for the industry defaults)', type: 'checks', full: true, options: [
        { value: 'inventory', label: 'Inventory' }, { value: 'manufacturing', label: 'Manufacturing' }, { value: 'jobs', label: 'Jobs & Projects' }, { value: 'fleet', label: 'Fleet & Trips' }] },
      { key: 'inventoryAccountId', label: 'Inventory control account', type: 'select', source: 'accounts', optional: '— not used —' },
      { key: 'cogsAccountId', label: 'Cost of goods sold', type: 'select', source: 'accounts', optional: '— not used —' },
      { key: 'stockClearingAccountId', label: 'Goods-received clearing', type: 'select', source: 'accounts', optional: '— not used —' },
      { key: 'stockAdjustmentAccountId', label: 'Stock adjustments', type: 'select', source: 'accounts', optional: '— not used —' },
      { key: 'conversionCostAccountId', label: 'Production overhead absorbed', type: 'select', source: 'accounts', optional: '— not used —' },
      { key: 'fleetExpenseAccountId', label: 'Fleet running costs', type: 'select', source: 'accounts', optional: '— not used —' },
    ],
    initial: (p) => ({
      industry: p?.industry ?? 'General', modules: [],
      ...Object.fromEntries(['inventoryAccountId', 'cogsAccountId', 'stockClearingAccountId', 'stockAdjustmentAccountId', 'conversionCostAccountId', 'fleetExpenseAccountId'].map((k) => [k, p?.profile?.[k] ? String(p.profile[k]) : ''])),
    }),
    body: (v) => ({
      industry: v.industry, modules: (v.modules as string[]).length ? v.modules : null,
      inventoryAccountId: n(v.inventoryAccountId), cogsAccountId: n(v.cogsAccountId), stockClearingAccountId: n(v.stockClearingAccountId),
      stockAdjustmentAccountId: n(v.stockAdjustmentAccountId), conversionCostAccountId: n(v.conversionCostAccountId), fleetExpenseAccountId: n(v.fleetExpenseAccountId),
    }),
    done: (r) => `Industry set to ${r.industry}; modules: ${(r.modules as string[]).join(', ') || 'none'}`,
  },
};

export function ModuleForm({ spec, companyId, onClose, onDone }: { spec: FormSpec; companyId: number; onClose: () => void; onDone: (msg: string, target?: string) => void }) {
  const qc = useQueryClient();
  const needed = useMemo(() => {
    const s = new Set<Source>(['profile']);
    spec.fields.forEach((f) => f.source && s.add(f.source));
    if (spec.lines) s.add('items');
    return [...s];
  }, [spec]);
  const paths = [...new Set(needed.map((s) => SOURCES[s].path))];
  const results = useQueries({ queries: paths.map((p) => ({ queryKey: ['sheet', companyId, p], queryFn: () => api.get<unknown>(p, companyId) })) });
  const data = Object.fromEntries(paths.map((p, i) => [p, results[i].data]));
  const loadError = results.find((r) => r.isError)?.error;
  const profile = data['/modules/profile'] as J | undefined;

  const [values, setValues] = useState<J>(() => ({ date: today(), plannedDate: today(), ...(spec.initial?.() ?? {}) }));
  const [seeded, setSeeded] = useState(false);
  useEffect(() => {
    if (!seeded && profile && spec.initial) { setValues((v) => ({ ...v, ...spec.initial!(profile) })); setSeeded(true); }
  }, [profile, seeded, spec]);
  const [lines, setLines] = useState<{ itemId: string; quantity: string; unitCost: string }[]>([{ itemId: '', quantity: '', unitCost: '' }]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const esc = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);

  const optionsFor = (f: Field) => f.options ?? (f.source && data[SOURCES[f.source].path] ? SOURCES[f.source].pick(data[SOURCES[f.source].path]) : []);
  const itemOptions = data['/inventory/stockable-items'] ? SOURCES.items.pick(data['/inventory/stockable-items']) : [];

  const submit = async () => {
    setError('');
    try {
      for (const f of spec.fields) if (f.required && (!f.showIf || f.showIf(values)) && !values[f.key]) throw new Error(`${f.label} is required.`);
      const ls = spec.lines ? lines.filter((l) => l.itemId).map((l) => ({ itemId: Number(l.itemId), quantity: Number(l.quantity), unitCost: l.unitCost === '' ? null : Number(l.unitCost) })) : [];
      if (spec.lines && ls.length === 0) throw new Error('Add at least one item line.');
      if (spec.lines?.cost === 'required' && ls.some((l) => l.unitCost == null)) throw new Error('Enter a unit cost on every line.');
      setBusy(true);
      const body = spec.body ? spec.body(values, ls) : values;
      const res = await (spec.method === 'PUT'
        ? fetch(`/api${spec.path(values)}`, { method: 'PUT', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-ASCO': '1', 'X-Company-Id': String(companyId) }, body: JSON.stringify(body) })
            .then(async (r) => { const t = await r.text(); const j = t ? JSON.parse(t) : {}; if (!r.ok) throw new Error(j.detail ?? `Failed (${r.status})`); return j; })
        : api.post<J>(spec.path(values), body, companyId));
      await qc.invalidateQueries();
      onDone(spec.done(res ?? {}), spec.target);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const visible = spec.fields.filter((f) => !f.showIf || f.showIf(values));
  return (
    <div className="modal-back" onMouseDown={onClose}>
      <div className="modal" onMouseDown={(e) => e.stopPropagation()}>
        <div className="modal-title"><span>{spec.title}</span><button className="icon-btn" onClick={onClose} aria-label="Close"><X size={16} /></button></div>
        <div className="modal-body">
          {loadError && <div className="form-error">{loadError.message}</div>}
          <div className="form-grid">
            {visible.map((f) => (
              <label key={f.key} className={f.full ? 'full' : ''}>{f.label}
                {f.type === 'select' ? (
                  <select value={values[f.key] ?? ''} onChange={(e) => setValues({ ...values, [f.key]: e.target.value })}>
                    <option value="">{f.optional ?? '— choose —'}</option>
                    {optionsFor(f).map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                  </select>
                ) : f.type === 'checks' ? (
                  <span className="checks">{optionsFor(f).map((o) => (
                    <label key={o.value} className="check"><input type="checkbox" checked={(values[f.key] ?? []).includes(o.value)}
                      onChange={(e) => setValues({ ...values, [f.key]: e.target.checked ? [...(values[f.key] ?? []), o.value] : (values[f.key] ?? []).filter((x: string) => x !== o.value) })} /> {o.label}</label>
                  ))}</span>
                ) : (
                  <input type={f.type === 'number' ? 'text' : f.type} inputMode={f.type === 'number' ? 'decimal' : undefined} value={values[f.key] ?? ''} onChange={(e) => setValues({ ...values, [f.key]: e.target.value })} />
                )}
              </label>
            ))}
          </div>
          {spec.lines && (
            <>
              <table className="form-lines">
                <thead><tr><th>{spec.lines.label}</th><th>Quantity{spec.lines.signed ? ' (±)' : ''}</th>{spec.lines.cost && <th>Unit cost{spec.lines.cost === 'optional' ? ' (optional)' : ''}</th>}<th /></tr></thead>
                <tbody>
                  {lines.map((l, i) => (
                    <tr key={i}>
                      <td><select value={l.itemId} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, itemId: e.target.value } : x)))}><option value="">— item —</option>{itemOptions.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}</select></td>
                      <td><input className="n" value={l.quantity} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, quantity: e.target.value } : x)))} /></td>
                      {spec.lines!.cost && <td><input className="n" value={l.unitCost} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, unitCost: e.target.value } : x)))} /></td>}
                      <td>{lines.length > 1 && <button className="icon-btn" onClick={() => setLines(lines.filter((_, j) => j !== i))} aria-label="Remove line"><X size={14} /></button>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <button className="btn ghost" onClick={() => setLines([...lines, { itemId: '', quantity: '', unitCost: '' }])}>+ Add line</button>
              {itemOptions.length === 0 && <div className="muted">No goods items yet — create items with kind "Goods" first (Items → Items, or the API).</div>}
            </>
          )}
          {error && <div className="form-error">{error}</div>}
        </div>
        <div className="modal-foot">
          <span className="muted">Saved through the ASCO API; any GL impact posts via C-ERP’s engine.</span>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={submit} disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        </div>
      </div>
    </div>
  );
}
