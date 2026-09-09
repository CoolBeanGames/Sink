import type { LibraryView, Playlist } from '../types';

const categories: { id: Exclude<LibraryView, 'playlist'>; label: string; icon: string }[] = [
  { id: 'artists', label: 'Artists', icon: '◉' },
  { id: 'albums', label: 'Albums', icon: '▣' },
  { id: 'genres', label: 'Genres', icon: '◇' },
  { id: 'songs', label: 'Songs', icon: '♫' },
];

type SidebarProps = {
  view: LibraryView;
  activePlaylistId: string | null;
  playlists: Playlist[];
  onNavigate: (view: LibraryView, playlistId?: string) => void;
  onCreatePlaylist: () => void;
};

export function Sidebar({ view, activePlaylistId, playlists, onNavigate, onCreatePlaylist }: SidebarProps) {
  return (
    <aside className="sidebar">
      <div className="brand"><span className="brand-mark">S</span><span>Sink</span></div>
      <nav aria-label="Music library">
        <p className="eyebrow">Library</p>
        {categories.map((item) => (
          <button key={item.id} className={`nav-item ${view === item.id ? 'active' : ''}`} onClick={() => onNavigate(item.id)}>
            <span aria-hidden="true">{item.icon}</span>{item.label}
          </button>
        ))}
      </nav>
      <div className="playlist-heading">
        <p className="eyebrow">Playlists</p>
        <button className="icon-button" aria-label="Create playlist" title="Create playlist" onClick={onCreatePlaylist}>＋</button>
      </div>
      <div className="playlist-list">
        {playlists.map((playlist) => (
          <button key={playlist.id} className={`nav-item ${view === 'playlist' && activePlaylistId === playlist.id ? 'active' : ''}`} onClick={() => onNavigate('playlist', playlist.id)}>
            <span aria-hidden="true">≡</span>{playlist.name}<small>{playlist.trackIds.length}</small>
          </button>
        ))}
        {playlists.length === 0 && <p className="empty-copy">No playlists yet</p>}
      </div>
      <button className="new-playlist" onClick={onCreatePlaylist}>＋ New playlist</button>
    </aside>
  );
}
