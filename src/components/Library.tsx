import { useRef, useState } from 'react';
import type { LibraryView, Playlist, Track } from '../types';

type LibraryProps = {
  view: LibraryView;
  tracks: Track[];
  playlists: Playlist[];
  activePlaylistId: string | null;
  drilldown: string | null;
  onDrilldown: (value: string) => void;
  onPlay: (track: Track) => void;
  onBack: () => void;
  onOpenDuplicates: () => void;
};

const unique = (items: string[]) => [...new Set(items)].sort((a, b) => a.localeCompare(b));
const formatTime = (seconds: number) => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;

export function Library({ view, tracks, playlists, activePlaylistId, drilldown, onDrilldown, onPlay, onBack, onOpenDuplicates }: LibraryProps) {
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const selectionAnchor = useRef<number | null>(null);
  const rowRefs = useRef(new Map<string, HTMLButtonElement>());
  const playlist = playlists.find((item) => item.id === activePlaylistId);
  const title = view === 'playlist' ? playlist?.name ?? 'Playlist' : `${view[0].toUpperCase()}${view.slice(1)}`;
  let visibleTracks = tracks;
  if (view === 'playlist') visibleTracks = tracks.filter((track) => playlist?.trackIds.includes(track.id));
  if (drilldown && view === 'albums') visibleTracks = tracks.filter((track) => track.album === drilldown);
  if (drilldown && view === 'artists') visibleTracks = tracks.filter((track) => track.artist === drilldown);
  if (drilldown && view === 'genres') visibleTracks = tracks.filter((track) => track.genre === drilldown);

  const groupValues = view === 'albums' ? unique(tracks.map((track) => track.album))
    : view === 'artists' ? unique(tracks.map((track) => track.artist))
    : view === 'genres' ? unique(tracks.map((track) => track.genre)) : [];

  const selectRange = (from: number, to: number) => new Set(visibleTracks.slice(Math.min(from, to), Math.max(from, to) + 1).map((track) => track.id));
  const selectTrack = (index: number, event: React.MouseEvent) => {
    const id = visibleTracks[index].id;
    if (event.shiftKey && selectionAnchor.current !== null) {
      setSelectedIds(selectRange(selectionAnchor.current, index));
    } else if (event.ctrlKey || event.metaKey) {
      setSelectedIds((current) => { const next = new Set(current); if (next.has(id)) next.delete(id); else next.add(id); return next; });
      selectionAnchor.current = index;
    } else {
      setSelectedIds(new Set([id]));
      selectionAnchor.current = index;
    }
  };

  const keyboardSelect = (index: number, event: React.KeyboardEvent) => {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
    event.preventDefault();
    const nextIndex = Math.max(0, Math.min(visibleTracks.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)));
    if (event.shiftKey) {
      const anchor = selectionAnchor.current ?? index;
      selectionAnchor.current = anchor;
      setSelectedIds(selectRange(anchor, nextIndex));
    } else {
      selectionAnchor.current = nextIndex;
      setSelectedIds(new Set([visibleTracks[nextIndex].id]));
    }
    rowRefs.current.get(visibleTracks[nextIndex].id)?.focus();
  };

  return (
    <main className="library">
      <header className="library-header">
        <div>
          {drilldown && <button className="back-button" onClick={onBack}>← {title}</button>}
          <p className="eyebrow">Music collection</p>
          <h1>{drilldown ?? title}</h1>
          <p className="subtitle">{drilldown || view === 'songs' || view === 'playlist' ? `${visibleTracks.length} tracks` : `${groupValues.length} ${title.toLowerCase()}`}</p>
        </div>
        <div className="header-actions"><button className="tool-button" onClick={onOpenDuplicates}>⊙ Duplicates</button><label className="search"><span>⌕</span><input aria-label="Search library" placeholder="Search your library" /></label></div>
      </header>

      {!drilldown && ['albums', 'artists', 'genres'].includes(view) ? (
        <section className="card-grid" aria-label={title}>
          {groupValues.map((value, index) => {
            const groupTracks = tracks.filter((track) => track[view === 'albums' ? 'album' : view === 'artists' ? 'artist' : 'genre'] === value);
            const detail = view === 'albums' ? groupTracks[0]?.artist : `${groupTracks.length} tracks`;
            return (
              <button className="collection-card" key={value} onDoubleClick={() => onDrilldown(value)} onClick={(event) => event.currentTarget.focus()}>
                <span className={`cover cover-${index % 4}`} aria-hidden="true"><i /><b>{value.charAt(0)}</b></span>
                <strong>{value}</strong><small>{detail}</small>
              </button>
            );
          })}
        </section>
      ) : (
        <section className="track-table" aria-label={`${title} tracks`}>
          {selectedIds.size > 0 && <div className="selection-bar" role="status"><span>{selectedIds.size} track{selectedIds.size === 1 ? '' : 's'} selected</span><button onClick={() => setSelectedIds(new Set())}>Clear</button></div>}
          <div className="track-row table-heading"><span>#</span><span>Title</span><span>Album</span><span>Genre</span><span>Time</span></div>
          {visibleTracks.map((track, index) => (
            <button ref={(element) => { if (element) rowRefs.current.set(track.id, element); else rowRefs.current.delete(track.id); }} className={`track-row ${selectedIds.has(track.id) ? 'selected' : ''}`} aria-pressed={selectedIds.has(track.id)} key={track.id} draggable onClick={(event) => selectTrack(index, event)} onKeyDown={(event) => keyboardSelect(index, event)} onDragStart={(event) => { event.dataTransfer.effectAllowed = 'copy'; event.dataTransfer.setData('application/x-sink-track', track.id); }} onDoubleClick={() => onPlay(track)} title="Double-click to play · Drag to add to a playlist">
              <span className="track-number">{index + 1}</span>
              <span className="track-title"><i className={`mini-cover cover-${index % 4}`} /><span><strong>{track.title}</strong><small>{track.artist}</small></span></span>
              <span>{track.album}</span><span>{track.genre}</span><span>{formatTime(track.duration)}</span>
            </button>
          ))}
          {visibleTracks.length === 0 && <div className="empty-state"><span>♫</span><h2>Nothing here yet</h2><p>Add tracks to fill this collection.</p></div>}
        </section>
      )}
    </main>
  );
}
