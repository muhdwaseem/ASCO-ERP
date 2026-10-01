// Date strip above a dated report: From/To with quick picks, As of, a year or a month.
import { CalendarRange } from 'lucide-react';
import { asOfPresets, rangePresets, todayIso, type DateMode, type DateState } from '../modules/reportDates';

export function ReportDates({ dm, value, onChange }: { dm: DateMode; value: DateState; onChange: (v: DateState) => void }) {
  const set = (patch: Partial<DateState>) => onChange({ ...value, ...patch });
  const today = todayIso();
  const thisYear = Number(today.slice(0, 4));
  const hint = dm.field ? 'Filters the list by its date column.' : 'Recalculated from the ledger for the dates chosen.';
  return (
    <div className="entry-header report-dates">
      <CalendarRange size={18} color="#107c41" style={{ alignSelf: 'center' }} />
      {dm.mode === 'range' && (
        <>
          <label>Period
            <select value="" onChange={(e) => { const p = rangePresets(today).find((x) => x.label === e.target.value); if (p) set({ from: p.from, to: p.to }); }}>
              <option value="">Quick pick…</option>
              {rangePresets(today).map((p) => <option key={p.label}>{p.label}</option>)}
            </select>
          </label>
          <label>From <input type="date" value={value.from} max={value.to} onChange={(e) => e.target.value && set({ from: e.target.value })} /></label>
          <label>To <input type="date" value={value.to} min={value.from} onChange={(e) => e.target.value && set({ to: e.target.value })} /></label>
        </>
      )}
      {dm.mode === 'asOf' && (
        <>
          <label>Quick pick
            <select value="" onChange={(e) => { const p = asOfPresets(today).find((x) => x.label === e.target.value); if (p) set({ asOf: p.date }); }}>
              <option value="">Choose…</option>
              {asOfPresets(today).map((p) => <option key={p.label}>{p.label}</option>)}
            </select>
          </label>
          <label>As of <input type="date" value={value.asOf} onChange={(e) => e.target.value && set({ asOf: e.target.value })} /></label>
        </>
      )}
      {dm.mode === 'year' && (
        <label>Year
          <select value={value.year} onChange={(e) => set({ year: e.target.value })}>
            {Array.from({ length: 8 }, (_, i) => String(thisYear + 1 - i)).map((y) => <option key={y}>{y}</option>)}
          </select>
        </label>
      )}
      {dm.mode === 'month' && <label>Month <input type="month" value={value.month} onChange={(e) => e.target.value && set({ month: e.target.value })} /></label>}
      <span className="hint">{hint}</span>
    </div>
  );
}
