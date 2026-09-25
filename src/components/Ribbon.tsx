import type { LucideIcon } from 'lucide-react';
import { ChevronUp } from 'lucide-react';
import { SCREENS } from '../modules/registry';

export interface Action { label: string; icon: LucideIcon; color: string; run: () => void; small?: boolean; disabled?: boolean; title?: string }
export interface ActionGroup { group: string; actions: Action[]; position?: 'start' | 'end' }

interface Props {
  tab: string;
  active: string;
  onOpen: (id: string) => void;
  extra: ActionGroup[];
  onCollapse: () => void;
}

function Big({ a, on }: { a: Action; on?: boolean }) {
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

export function Ribbon({ tab, active, onOpen, extra, onCollapse }: Props) {
  const screens = SCREENS.filter((x) => x.tab === tab);
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
