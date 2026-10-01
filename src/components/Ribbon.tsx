import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { LucideIcon } from 'lucide-react';
import { ChevronDown, ChevronRight, ChevronUp } from 'lucide-react';
import { SCREENS, type Screen } from '../modules/registry';

export interface Action {
  label: string; icon: LucideIcon; color: string; run: () => void; small?: boolean; disabled?: boolean; title?: string;
  /** What the action requires in live mode; 'live' = only meaningful against the API. */
  needs?: 'post' | 'payroll' | 'admin' | 'live';
  /** Turns the button into an Excel-style dropdown (like Get Data ▾) with ▸ fly-out sub-menus. */
  menu?: MenuItem[];
}
export interface MenuItem { label: string; icon?: LucideIcon; color?: string; run?: () => void; items?: MenuItem[] }
export interface ActionGroup { group: string; actions: Action[]; position?: 'start' | 'end' }

interface Props {
  tab: string;
  active: string;
  onOpen: (id: string) => void;
  extra: ActionGroup[];
  onCollapse: () => void;
  /** Hides screens of modules the company hasn't enabled. */
  screenFilter?: (s: Screen) => boolean;
}

/** One level of the dropdown. `path` holds the highlighted index per level; the highlighted
 * item's children fly out to the right (mouse hover or →), Enter/click runs a leaf. */
function MenuLevel({ items, level, path, setPath, close }: { items: MenuItem[]; level: number; path: number[]; setPath: (p: number[]) => void; close: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  // Keep fly-outs on screen: lift one that would run past the bottom, flip one that would run off the right.
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || level === 0) return;
    el.style.top = '-4px'; el.style.left = '100%'; el.style.right = 'auto';
    const r = el.getBoundingClientRect();
    const over = r.bottom - window.innerHeight + 8;
    if (over > 0) el.style.top = `${-4 - Math.min(over, r.top - 8)}px`;
    if (r.right > window.innerWidth - 4) { el.style.left = 'auto'; el.style.right = '100%'; }
  });
  return (
    <div ref={ref} className={level === 0 ? 'rb-menu' : 'rb-menu rb-sub'} role="menu">
      {items.map((it, i) => {
        const Icon = it.icon;
        const on = path[level] === i;
        return (
          <div key={it.label} role="menuitem" aria-haspopup={!!it.items} className={`rb-mi${on ? ' on' : ''}${level === 0 ? ' top' : ''}`}
            onMouseEnter={() => setPath([...path.slice(0, level), i])}
            onClick={(e) => { e.stopPropagation(); if (it.items) setPath([...path.slice(0, level), i, 0]); else { it.run?.(); close(); } }}>
            <span className="rb-mi-icon">{Icon && <Icon size={level === 0 ? 20 : 16} strokeWidth={1.7} color={it.color} />}</span>
            <span className="rb-mi-label">{it.label}</span>
            {it.items && <ChevronRight size={14} className="rb-mi-arrow" />}
            {on && it.items && <MenuLevel items={it.items} level={level + 1} path={path} setPath={setPath} close={close} />}
          </div>
        );
      })}
    </div>
  );
}

function DropBig({ a }: { a: Action }) {
  const Icon = a.icon;
  const [open, setOpen] = useState(false);
  const [path, setPath] = useState<number[]>([]);
  const [pos, setPos] = useState({ left: 0, top: 0 });
  const btn = useRef<HTMLButtonElement>(null);
  const box = useRef<HTMLDivElement>(null);
  const close = () => { setOpen(false); setPath([]); };
  useEffect(() => {
    if (!open) return;
    const away = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node) && !btn.current?.contains(e.target as Node)) close(); };
    const keys = (e: KeyboardEvent) => {
      const items = a.menu ?? [];
      const at = (p: number[]) => p.reduce<{ list: MenuItem[] }>((acc, idx, lvl) => (lvl < p.length - 1 ? { list: acc.list[idx]?.items ?? [] } : acc), { list: items }).list;
      const p = path.length ? path : [0];
      const list = at(p);
      const cur = list[p[p.length - 1]];
      const move = (d: number) => setPath([...p.slice(0, -1), (p[p.length - 1] + d + list.length) % list.length]);
      if (e.key === 'Escape') { e.preventDefault(); if (p.length > 1) setPath(p.slice(0, -1)); else close(); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); path.length ? move(1) : setPath([0]); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
      else if (e.key === 'ArrowRight' && cur?.items) { e.preventDefault(); setPath([...p, 0]); }
      else if (e.key === 'ArrowLeft' && p.length > 1) { e.preventDefault(); setPath(p.slice(0, -1)); }
      else if (e.key === 'Enter' && cur) { e.preventDefault(); if (cur.items) setPath([...p, 0]); else { cur.run?.(); close(); } }
    };
    document.addEventListener('mousedown', away);
    document.addEventListener('keydown', keys, true);
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', keys, true); };
  }, [open, path, a.menu]);
  const toggle = () => {
    if (open) return close();
    const r = btn.current!.getBoundingClientRect();
    setPos({ left: r.left, top: r.bottom + 2 });
    setOpen(true);
  };
  return (
    <>
      <button ref={btn} className={`rb-big rb-drop${open ? ' on' : ''}`} onClick={toggle} disabled={a.disabled} title={a.title ?? a.label} aria-haspopup="menu" aria-expanded={open}>
        <Icon size={30} strokeWidth={1.6} color={a.color} />
        <span>{a.label} <ChevronDown size={11} style={{ verticalAlign: 'middle' }} /></span>
      </button>
      {open && (
        <div ref={box} className="rb-menu-host" style={{ left: pos.left, top: pos.top }}>
          <MenuLevel items={a.menu ?? []} level={0} path={path} setPath={setPath} close={close} />
        </div>
      )}
    </>
  );
}

function Big({ a, on }: { a: Action; on?: boolean }) {
  if (a.menu) return <DropBig a={a} />;
  const Icon = a.icon;
  return (
    <button className={`rb-big${on ? ' on' : ''}`} onClick={a.run} disabled={a.disabled} title={a.title ?? a.label}>
      <Icon size={30} strokeWidth={1.6} color={a.color} />
      <span>{a.label}</span>
    </button>
  );
}

function Small({ a }: { a: Action }) {
  const Icon = a.icon;
  return (
    <button className="rb-small" onClick={a.run} disabled={a.disabled} title={a.title ?? a.label}>
      <Icon size={16} strokeWidth={1.8} color={a.color} />
      <span>{a.label}</span>
    </button>
  );
}

function Group({ label, actions, active }: { label: string; actions: (Action & { id?: string })[]; active: string }) {
  const bigs = actions.filter((a) => !a.small);
  const smalls = actions.filter((a) => a.small);
  const stacks: Action[][] = [];
  for (let i = 0; i < smalls.length; i += 3) stacks.push(smalls.slice(i, i + 3));
  return (
    <div className="rb-group">
      <div className="rb-items">
        {bigs.map((a) => <Big key={a.label} a={a} on={a.id === active} />)}
        {stacks.map((st, i) => <div key={i} className="rb-stack">{st.map((a) => <Small key={a.label} a={a} />)}</div>)}
      </div>
      <div className="rb-label">{label}</div>
    </div>
  );
}

export function Ribbon({ tab, active, onOpen, extra, onCollapse, screenFilter }: Props) {
  const screens = SCREENS.filter((x) => x.tab === tab && !x.menuOnly && (!screenFilter || screenFilter(x)));
  const groups = [...new Set(screens.map((x) => x.group))];
  const pre = extra.filter((g) => g.position === 'start');
  const post = extra.filter((g) => g.position !== 'start');
  return (
    <div className="ribbon">
      {pre.map((g) => <Group key={g.group} label={g.group} actions={g.actions} active={active} />)}
      {groups.map((g) => (
        <Group
          key={g}
          label={g}
          active={active}
          actions={screens.filter((x) => x.group === g).map((x) => ({ id: x.id, label: x.label, icon: x.icon, color: x.color, run: () => onOpen(x.id), title: x.planned ? `${x.label} (planned)` : x.label }))}
        />
      ))}
      {post.map((g) => <Group key={g.group} label={g.group} actions={g.actions} active={active} />)}
      <button className="rb-collapse" onClick={onCollapse} title="Collapse the Ribbon" aria-label="Collapse the Ribbon"><ChevronUp size={16} /></button>
    </div>
  );
}
