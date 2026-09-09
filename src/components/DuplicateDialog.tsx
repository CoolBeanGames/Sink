import { useMemo, useState } from 'react';
import type { Track } from '../types';

type DuplicateDialogProps = {
  tracks: Track[];
  onClose: () => void;
  onRemove: (ids: Set<string>) => void;
};

type MatchMode = 'fileName' | 'title';

const normalize = (value: string) => value.trim().toLocaleLowerCase();

export function DuplicateDialog({ tracks, onClose, onRemove }: DuplicateDialogProps) {
  const [mode, setMode] = useState<MatchMode>('fileName');
  const groups = useMemo(() => {
    const matches = new Map<string, Track[]>();
    tracks.forEach((track) => {
      const key = normalize(track[mode]);
      matches.set(key, [...(matches.get(key) ?? []), track]);
    });
    return [...matches.values()].filter((group) => group.length > 1);
  }, [mode, tracks]);
  const suggested = useMemo(() => new Set(groups.flatMap((group) => group.slice(1).map((track) => track.id))), [groups]);
  const [overrides, setOverrides] = useState<Record<string, boolean>>({});
  const selected = new Set([...suggested].filter((id) => overrides[id] !== false));
  Object.entries(overrides).forEach(([id, remove]) => remove && selected.add(id));

  return (
    <div className="modal-scrim" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <section className="duplicate-dialog" role="dialog" aria-modal="true" aria-labelledby="duplicate-title">
        <header><div><p className="eyebrow">Library tools</p><h2 id="duplicate-title">Find duplicates</h2><p>Preview matches and choose exactly what to remove.</p></div><button className="close-button" aria-label="Close" onClick={onClose}>×</button></header>
        <div className="match-switch" aria-label="Duplicate matching method">
          <button className={mode === 'fileName' ? 'active' : ''} onClick={() => { setMode('fileName'); setOverrides({}); }}>File name</button>
          <button className={mode === 'title' ? 'active' : ''} onClick={() => { setMode('title'); setOverrides({}); }}>Track name</button>
        </div>
        <div className="duplicate-preview">
          {groups.map((group) => <div className="duplicate-group" key={`${mode}-${group[0][mode]}`}>
            <strong>{group[0][mode]}</strong><small>{group.length} copies</small>
            {group.map((track, index) => {
              const checked = index > 0 ? overrides[track.id] !== false : overrides[track.id] === true;
              return <label key={track.id}><input type="checkbox" checked={checked} onChange={(event) => setOverrides((current) => ({ ...current, [track.id]: event.target.checked }))} /><span><b>{track.title}</b><small>{track.artist} · {track.album}</small></span><em>{index === 0 && !checked ? 'Keep' : checked ? 'Remove' : 'Keep'}</em></label>;
            })}
          </div>)}
          {!groups.length && <div className="no-duplicates"><span>✓</span><h3>No duplicates found</h3><p>There are no repeated {mode === 'fileName' ? 'file names' : 'track names'} in this library.</p></div>}
        </div>
        <footer><span>{selected.size} track{selected.size === 1 ? '' : 's'} selected</span><div><button onClick={onClose}>Cancel</button><button className="danger-button" disabled={!selected.size} onClick={() => onRemove(selected)}>Remove selected</button></div></footer>
      </section>
    </div>
  );
}
