import { useEffect, useState } from 'react';
import { Library } from './components/Library';
import { IpodDock } from './components/IpodDock';
import { DuplicateDialog } from './components/DuplicateDialog';
import { Player } from './components/Player';
import { Sidebar } from './components/Sidebar';
import { seedTracks } from './data';
import { filesFromDrop, trackFromFile } from './importMusic';
import type { LibraryView, Playlist, Track } from './types';
import './styles.css';

export default function App() {
  const [view, setView] = useState<LibraryView>('albums');
  const [drilldown, setDrilldown] = useState<string | null>(null);
  const [activePlaylistId, setActivePlaylistId] = useState<string | null>(null);
  const [playlists, setPlaylists] = useState<Playlist[]>([{ id: 'favorites', name: 'Favorites', trackIds: ['1', '3', '5'] }]);
  const [tracks, setTracks] = useState<Track[]>(seedTracks);
  const [nowPlaying, setNowPlaying] = useState<Track | null>(null);
  const [isPlaying, setIsPlaying] = useState(false);
  const [position, setPosition] = useState(0);
  const [isDragging, setIsDragging] = useState(false);
  const [importMessage, setImportMessage] = useState<string | null>(null);
  const [showDuplicates, setShowDuplicates] = useState(false);

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

  const importDrop = async (event: React.DragEvent) => {
    event.preventDefault(); setIsDragging(false);
    const files = await filesFromDrop(event.dataTransfer);
    const imported = files.map(trackFromFile);
    if (imported.length) setTracks((current) => [...current, ...imported]);
    setImportMessage(imported.length ? `Imported ${imported.length} track${imported.length === 1 ? '' : 's'}` : 'No new audio files found');
    window.setTimeout(() => setImportMessage(null), 3200);
  };

  const addToPlaylist = (playlistId: string, trackId: string) => {
    const playlist = playlists.find((item) => item.id === playlistId);
    const added = Boolean(playlist && !playlist.trackIds.includes(trackId));
    if (added) setPlaylists((current) => current.map((item) => item.id === playlistId ? { ...item, trackIds: [...item.trackIds, trackId] } : item));
    setImportMessage(added ? `Added to ${playlist?.name ?? 'playlist'}` : `Already in ${playlist?.name ?? 'playlist'}`);
    window.setTimeout(() => setImportMessage(null), 2400);
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
    <div className="app-shell" onDragEnter={(event) => { event.preventDefault(); setIsDragging(true); }} onDragOver={(event) => event.preventDefault()} onDragLeave={(event) => { if (event.currentTarget === event.target) setIsDragging(false); }} onDrop={importDrop}>
      <Sidebar view={view} activePlaylistId={activePlaylistId} playlists={playlists} onNavigate={navigate} onCreatePlaylist={createPlaylist} onAddToPlaylist={addToPlaylist} />
      <Library view={view} tracks={tracks} playlists={playlists} activePlaylistId={activePlaylistId} drilldown={drilldown} onDrilldown={setDrilldown} onBack={() => setDrilldown(null)} onPlay={playTrack} onOpenDuplicates={() => setShowDuplicates(true)} />
      <IpodDock />
      <Player track={nowPlaying} isPlaying={isPlaying} position={position} onToggle={() => nowPlaying && setIsPlaying((value) => !value)} onSeek={setPosition} onPrevious={() => skip(-1)} onNext={() => skip(1)} />
      {isDragging && <div className="drop-overlay"><div><span>↓</span><h2>Drop music to import</h2><p>Files and nested folders are welcome</p></div></div>}
      {importMessage && <div className="import-toast" role="status">✓ {importMessage}</div>}
      {showDuplicates && <DuplicateDialog tracks={tracks} onClose={() => setShowDuplicates(false)} onRemove={(ids) => { setTracks((current) => current.filter((track) => !ids.has(track.id))); setPlaylists((current) => current.map((playlist) => ({ ...playlist, trackIds: playlist.trackIds.filter((id) => !ids.has(id)) }))); if (nowPlaying && ids.has(nowPlaying.id)) { setNowPlaying(null); setIsPlaying(false); setPosition(0); } setShowDuplicates(false); setImportMessage(`Removed ${ids.size} duplicate${ids.size === 1 ? '' : 's'}`); }} />}
    </div>
  );
}
