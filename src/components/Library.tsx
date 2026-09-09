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
};

const unique = (items: string[]) => [...new Set(items)].sort((a, b) => a.localeCompare(b));
const formatTime = (seconds: number) => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;

export function Library({ view, tracks, playlists, activePlaylistId, drilldown, onDrilldown, onPlay, onBack }: LibraryProps) {
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

  return (
    <main className="library">
      <header className="library-header">
        <div>
          {drilldown && <button className="back-button" onClick={onBack}>← {title}</button>}
          <p className="eyebrow">Music collection</p>
          <h1>{drilldown ?? title}</h1>
          <p className="subtitle">{drilldown || view === 'songs' || view === 'playlist' ? `${visibleTracks.length} tracks` : `${groupValues.length} ${title.toLowerCase()}`}</p>
        </div>
        <label className="search"><span>⌕</span><input aria-label="Search library" placeholder="Search your library" /></label>
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
          <div className="track-row table-heading"><span>#</span><span>Title</span><span>Album</span><span>Genre</span><span>Time</span></div>
          {visibleTracks.map((track, index) => (
            <button className="track-row" key={track.id} onDoubleClick={() => onPlay(track)} title="Double-click to play">
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
