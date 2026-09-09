import { useMemo, useState } from 'react';
import { Library } from './components/Library';
import { Sidebar } from './components/Sidebar';
import { seedTracks } from './data';
import type { LibraryView, Playlist, Track } from './types';
import './styles.css';

export default function App() {
  const [view, setView] = useState<LibraryView>('albums');
  const [drilldown, setDrilldown] = useState<string | null>(null);
  const [activePlaylistId, setActivePlaylistId] = useState<string | null>(null);
  const [playlists, setPlaylists] = useState<Playlist[]>([{ id: 'favorites', name: 'Favorites', trackIds: ['1', '3', '5'] }]);
  const [nowPlaying, setNowPlaying] = useState<Track | null>(null);
  const tracks = useMemo(() => seedTracks, []);

  const navigate = (nextView: LibraryView, playlistId?: string) => {
    setView(nextView); setDrilldown(null); setActivePlaylistId(playlistId ?? null);
  };

  const createPlaylist = () => {
    const name = window.prompt('Playlist name');
    if (!name?.trim()) return;
    const playlist = { id: crypto.randomUUID(), name: name.trim(), trackIds: [] };
    setPlaylists((current) => [...current, playlist]);
    navigate('playlist', playlist.id);
  };

  return (
    <div className="app-shell">
      <Sidebar view={view} activePlaylistId={activePlaylistId} playlists={playlists} onNavigate={navigate} onCreatePlaylist={createPlaylist} />
      <Library view={view} tracks={tracks} playlists={playlists} activePlaylistId={activePlaylistId} drilldown={drilldown} onDrilldown={setDrilldown} onBack={() => setDrilldown(null)} onPlay={setNowPlaying} />
      {nowPlaying && <div className="playing-toast" role="status"><span>▶</span><div><small>Now playing</small><strong>{nowPlaying.title}</strong></div></div>}
    </div>
  );
}
