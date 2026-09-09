import { useEffect, useMemo, useState } from 'react';
import { Library } from './components/Library';
import { Player } from './components/Player';
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
  const [isPlaying, setIsPlaying] = useState(false);
  const [position, setPosition] = useState(0);
  const tracks = useMemo(() => seedTracks, []);

  useEffect(() => {
    if (!isPlaying || !nowPlaying) return;
    const timer = window.setInterval(() => setPosition((current) => current >= nowPlaying.duration ? 0 : current + 1), 1000);
    return () => window.clearInterval(timer);
  }, [isPlaying, nowPlaying]);

  const playTrack = (track: Track) => {
    setNowPlaying(track); setPosition(0); setIsPlaying(true);
  };

  const skip = (direction: -1 | 1) => {
    if (!nowPlaying) return;
    const index = tracks.findIndex((track) => track.id === nowPlaying.id);
    playTrack(tracks[(index + direction + tracks.length) % tracks.length]);
  };

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
      <Library view={view} tracks={tracks} playlists={playlists} activePlaylistId={activePlaylistId} drilldown={drilldown} onDrilldown={setDrilldown} onBack={() => setDrilldown(null)} onPlay={playTrack} />
      <Player track={nowPlaying} isPlaying={isPlaying} position={position} onToggle={() => nowPlaying && setIsPlaying((value) => !value)} onSeek={setPosition} onPrevious={() => skip(-1)} onNext={() => skip(1)} />
    </div>
  );
}
