// Excel-style grid: column letters, row numbers, header row, active cell, range selection,
// keyboard navigation, copy-as-TSV, in-cell editing for editable sheets.
import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { Cell, Col, Row, RowMeta } from '../modules/registry';

export interface Sel { r: number; c: number; r2: number; c2: number }

export const colName = (i: number): string => (i < 26 ? String.fromCharCode(65 + i) : colName(Math.floor(i / 26) - 1) + colName(i % 26));

const money = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export function fmt(col: Col | undefined, v: unknown): string {
  if (v === undefined || v === null || v === '') return '';
  if (typeof v !== 'number') return String(v);
  switch (col?.type) {
    case 'money': return v === 0 ? '-' : v < 0 ? `(${money.format(-v)})` : money.format(v);
    case 'pct': return `${+(v * 100).toFixed(2)}%`;
    default: return v.toLocaleString('en-US', { maximumFractionDigits: 4 });
  }
}

export const cellValue = (row: Row | undefined, col: Col | undefined): Cell => (row && col ? (row[col.key] as Cell) : undefined);

interface Props {
  columns: Col[];
  rows: Row[];
  sel: Sel;
  onSel: (s: Sel) => void;
  zoom: number;
  editable?: (r: number, c: number) => boolean; // r = data row index (0-based), c = column index
  onEdit?: (r: number, c: number, value: string) => void;
  /** Search-as-you-type choices for a cell (customers, items, accounts…). r = data row index. */
  optionsFor?: (r: number, c: number) => string[] | undefined;
}

const MIN_ROWS = 60;

export function Grid({ columns, rows, sel, onSel, zoom, editable, onEdit, optionsFor }: Props) {
  const wrap = useRef<HTMLDivElement>(null);
  const dragging = useRef(false);
  const [edit, setEdit] = useState<{ r: number; c: number; value: string } | null>(null);
  const [hi, setHi] = useState(-1); // highlighted suggestion
  const options = edit ? optionsFor?.(edit.r - 1, edit.c) : undefined;
  const matches = options
    ? options.filter((o) => o.toLowerCase().includes(edit!.value.trim().toLowerCase())).slice(0, 8)
    : [];
  const totalRows = Math.max(MIN_ROWS, rows.length + 20);
  const totalCols = Math.max(columns.length + 8, 20);

  const top = Math.min(sel.r, sel.r2), bottom = Math.max(sel.r, sel.r2);
  const left = Math.min(sel.c, sel.c2), right = Math.max(sel.c, sel.c2);

  useEffect(() => {
    wrap.current?.querySelector<HTMLElement>(`[data-cell="${sel.r2}-${sel.c2}"]`)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, [sel.r2, sel.c2]);

  // keep keyboard focus on the grid unless an input is being used
  useEffect(() => {
    if (!edit && document.activeElement?.tagName !== 'INPUT' && document.activeElement?.tagName !== 'SELECT') wrap.current?.focus({ preventScroll: true });
  }, [edit, sel]);

  const canEdit = (r: number, c: number) => r >= 1 && !!editable?.(r - 1, c);

  const move = (dr: number, dc: number, extend: boolean) => {
    const r2 = Math.max(0, Math.min(totalRows - 1, (extend ? sel.r2 : sel.r) + dr));
    const c2 = Math.max(0, Math.min(totalCols - 1, (extend ? sel.c2 : sel.c) + dc));
    onSel(extend ? { ...sel, r2, c2 } : { r: r2, c: c2, r2, c2 });
  };

  // After a key-commit the input unmounts and blurs; ignore that second (stale) commit.
  const committed = useRef(false);
  const commit = (dr = 0, dc = 0, value?: string) => {
    if (committed.current) return;
    committed.current = true;
    if (edit) onEdit?.(edit.r - 1, edit.c, value ?? edit.value);
    setEdit(null);
    setHi(-1);
    if (dc && edit) {
      // Tab in an entry sheet jumps to the next editable column (skips computed ones);
      // past the last one it wraps to the first editable cell of the next row.
      let c = edit.c + dc;
      while (c >= 0 && c < columns.length && !canEdit(edit.r, c)) c += dc;
      if (c >= 0 && c < columns.length) onSel({ r: edit.r, c, r2: edit.r, c2: c });
      else if (dc > 0) {
        const first = columns.findIndex((_, i) => canEdit(edit.r + 1, i));
        if (first >= 0) onSel({ r: edit.r + 1, c: first, r2: edit.r + 1, c2: first });
      }
    } else if (dr) move(dr, 0, false);
  };
  /** Commit, completing the text to the highlighted (or first) suggestion like Excel's autocomplete. */
  const commitPick = (dr: number, dc: number) => {
    if (!edit) return;
    const typed = edit.value.trim();
    const exact = options?.some((o) => o === typed);
    const pick = typed && matches.length && !exact ? matches[hi >= 0 ? hi : 0] : hi >= 0 ? matches[hi] : undefined;
    commit(dr, dc, pick);
  };
  const startEdit = (r: number, c: number, value: string) => { committed.current = false; setHi(-1); setEdit({ r, c, value }); };

  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (edit) return;
    const k = e.key;
    if (k === 'ArrowDown') { e.preventDefault(); move(1, 0, e.shiftKey); }
    else if (k === 'ArrowUp') { e.preventDefault(); move(-1, 0, e.shiftKey); }
    else if (k === 'ArrowRight' || (k === 'Tab' && !e.shiftKey)) { e.preventDefault(); move(0, 1, e.shiftKey && k !== 'Tab'); }
    else if (k === 'ArrowLeft' || (k === 'Tab' && e.shiftKey)) { e.preventDefault(); move(0, -1, e.shiftKey && k !== 'Tab'); }
    else if (k === 'Enter' && !e.ctrlKey) { e.preventDefault(); move(e.shiftKey ? -1 : 1, 0, false); }
    else if (k === 'F2' && canEdit(sel.r, sel.c)) { e.preventDefault(); startEdit(sel.r, sel.c, String(cellValue(rows[sel.r - 1], columns[sel.c]) ?? '')); }
    else if ((k === 'Delete' || k === 'Backspace') && canEdit(sel.r, sel.c)) { e.preventDefault(); onEdit?.(sel.r - 1, sel.c, ''); }
    else if (k === 'Home') { e.preventDefault(); onSel({ r: e.ctrlKey ? 0 : sel.r, c: 0, r2: e.ctrlKey ? 0 : sel.r, c2: 0 }); }
    else if (e.ctrlKey && k.toLowerCase() === 'a') { e.preventDefault(); onSel({ r: 0, c: 0, r2: rows.length, c2: columns.length - 1 }); }
    else if (e.ctrlKey && k.toLowerCase() === 'c') { e.preventDefault(); copySelection(); }
    else if (k.length === 1 && !e.ctrlKey && !e.metaKey && canEdit(sel.r, sel.c)) { e.preventDefault(); startEdit(sel.r, sel.c, k); }
  };

  const copySelection = () => {
    const lines: string[] = [];
    for (let r = top; r <= bottom; r++) {
      const cells: string[] = [];
      for (let c = left; c <= right; c++) cells.push(r === 0 ? columns[c]?.label ?? '' : fmt(columns[c], cellValue(rows[r - 1], columns[c])));
      lines.push(cells.join('\t'));
    }
    navigator.clipboard?.writeText(lines.join('\n')).catch(() => {});
  };

  const down = (r: number, c: number, shift: boolean) => {
    if (edit) commit();
    wrap.current?.focus({ preventScroll: true }); // mousedown is preventDefault'ed, so take focus explicitly
    dragging.current = true;
    onSel(shift ? { ...sel, r2: r, c2: c } : { r, c, r2: r, c2: c });
  };

  useEffect(() => {
    const up = () => (dragging.current = false);
    window.addEventListener('mouseup', up);
    return () => window.removeEventListener('mouseup', up);
  }, []);

  const widths = Array.from({ length: totalCols }, (_, i) => columns[i]?.width ?? 80);

  return (
    <div className="grid-wrap" ref={wrap} tabIndex={0} onKeyDown={onKey} style={{ fontSize: `${(14.5 * zoom) / 100}px` }}>
      <table className="grid" style={{ width: widths.reduce((a, b) => a + (b * zoom) / 100, 44) }}>
        <colgroup>
          <col style={{ width: 44 }} />
          {widths.map((w, i) => <col key={i} style={{ width: (w * zoom) / 100 }} />)}
        </colgroup>
        <thead>
          <tr>
            <th className="corner" onMouseDown={() => onSel({ r: 0, c: 0, r2: rows.length, c2: columns.length - 1 })}><span /></th>
            {widths.map((_, c) => (
              <th key={c} className={c >= left && c <= right ? 'col-h on' : 'col-h'} onMouseDown={(e) => { e.preventDefault(); onSel({ r: 0, c, r2: rows.length, c2: c }); }}>
                {colName(c)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {Array.from({ length: totalRows }, (_, r) => {
            const row = r === 0 ? undefined : rows[r - 1];
            const m: RowMeta | undefined = row?._meta as RowMeta | undefined;
            const cls = [r === 0 ? 'hdr' : '', m?.style ? `st-${m.style}` : ''].join(' ');
            return (
              <tr key={r} className={cls}>
                <th className={r >= top && r <= bottom ? 'row-h on' : 'row-h'} onMouseDown={(e) => { e.preventDefault(); onSel({ r, c: 0, r2: r, c2: Math.max(columns.length - 1, 0) }); }}>{r + 1}</th>
                {widths.map((_, c) => {
                  const col = columns[c];
                  const inSel = r >= top && r <= bottom && c >= left && c <= right;
                  const active = r === sel.r && c === sel.c;
                  const isEditing = edit && edit.r === r && edit.c === c;
                  const raw = r === 0 ? col?.label : cellValue(row, col);
                  const num = r > 0 && col && ['money', 'number', 'pct'].includes(col.type ?? '');
                  const neg = typeof raw === 'number' && raw < 0;
                  const ed = r > 0 && canEdit(r, c);
                  return (
                    <td
                      key={c}
                      data-cell={`${r}-${c}`}
                      className={[isEditing ? 'editing' : '', inSel && !active ? 'sel' : '', active ? 'active' : '', num ? 'num' : '', neg ? 'neg' : '', ed ? 'editable' : '', c <= 1 && !num && m?.indent ? `ind-${m.indent}` : ''].join(' ')}
                      onMouseDown={(e) => { e.preventDefault(); down(r, c, e.shiftKey); }}
                      onMouseEnter={() => dragging.current && onSel({ ...sel, r2: r, c2: c })}
                      onDoubleClick={() => ed && startEdit(r, c, String(raw ?? ''))}
                    >
                      {isEditing ? (
                        <>
                          <input
                            autoFocus
                            className="cell-input"
                            value={edit.value}
                            onChange={(e) => { setEdit({ ...edit, value: e.target.value }); setHi(-1); }}
                            onBlur={() => commitPick(0, 0)}
                            onKeyDown={(e) => {
                              if (e.key === 'ArrowDown' && matches.length) { e.preventDefault(); setHi((h) => Math.min(matches.length - 1, h + 1)); }
                              else if (e.key === 'ArrowUp' && matches.length) { e.preventDefault(); setHi((h) => Math.max(-1, h - 1)); }
                              else if (e.key === 'Enter') { e.preventDefault(); commitPick(1, 0); wrap.current?.focus(); }
                              else if (e.key === 'Tab') { e.preventDefault(); commitPick(0, e.shiftKey ? -1 : 1); wrap.current?.focus(); }
                              else if (e.key === 'Escape') { committed.current = true; setEdit(null); setHi(-1); wrap.current?.focus(); }
                            }}
                          />
                          {matches.length > 0 && (
                            <div className="cell-suggest" onMouseDown={(e) => e.preventDefault()}>
                              {matches.map((m, i) => (
                                <div key={m} className={i === hi ? 'on' : ''} onMouseDown={(e) => { e.preventDefault(); commit(0, 0, m); wrap.current?.focus(); }}>{m}</div>
                              ))}
                            </div>
                          )}
                        </>
                      ) : (
                        r === 0 ? raw : fmt(col, raw)
                      )}
                    </td>
                  );
                })}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
