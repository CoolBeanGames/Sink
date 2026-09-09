import type { Track } from '../types';

type PlayerProps = {
  track: Track | null;
  isPlaying: boolean;
  position: number;
  onToggle: () => void;
  onSeek: (position: number) => void;
  onPrevious: () => void;
  onNext: () => void;
};

const formatTime = (seconds: number) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;

export function Player({ track, isPlaying, position, onToggle, onSeek, onPrevious, onNext }: PlayerProps) {
  const duration = track?.duration ?? 0;
  const progress = duration ? Math.min(100, (position / duration) * 100) : 0;

  return (
    <footer className="player" aria-label="Playback controls">
      <div className="now-playing">
        <span className="player-art cover-0" aria-hidden="true">{track?.title.charAt(0) ?? '♫'}</span>
        <span className="player-meta"><strong>{track?.title ?? 'Choose something to play'}</strong><small>{track?.artist ?? 'Your library is ready'}</small></span>
      </div>
      <div className="player-center">
        <div className="transport">
          <button aria-label="Previous track" onClick={onPrevious} disabled={!track}>◀│</button>
          <button className="play-button" aria-label={isPlaying ? 'Pause' : 'Play'} onClick={onToggle} disabled={!track}>{isPlaying ? 'Ⅱ' : '▶'}</button>
          <button aria-label="Next track" onClick={onNext} disabled={!track}>│▶</button>
        </div>
        <div className="timeline">
          <time>{formatTime(position)}</time>
          <input aria-label="Track progress" type="range" min="0" max={duration || 1} value={position} disabled={!track} onChange={(event) => onSeek(Number(event.target.value))} style={{ '--progress': `${progress}%` } as React.CSSProperties} />
          <time>-{formatTime(Math.max(0, duration - position))}</time>
        </div>
      </div>
      <div className="player-side"><span>VOL</span><span className="volume-line" /></div>
    </footer>
  );
}
