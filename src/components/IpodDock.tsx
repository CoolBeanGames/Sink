import { useEffect, useState } from 'react';

const syncActions = ['Sync all', 'Sync music', 'Sync changes', 'Sync podcasts'];

export function IpodDock() {
  const [connected, setConnected] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);

  useEffect(() => {
    if (!syncing) return;
    const timer = window.setTimeout(() => setSyncing(false), 3200);
    return () => window.clearTimeout(timer);
  }, [syncing]);

  const sync = () => { setMenuOpen(false); setSyncing(true); };

  return (
    <div className={`ipod-dock ${connected ? 'connected' : 'disconnected'} ${syncing ? 'syncing' : ''}`} onContextMenu={(event) => { event.preventDefault(); if (connected) setMenuOpen(true); }}>
      <button className="record" aria-label={connected ? 'Connected iPod' : 'Connect demo iPod'} onClick={() => !connected && setConnected(true)} title={connected ? 'River’s iPod · 160 GB' : 'No iPod connected · Click to simulate connection'}>
        <span className="record-rings" />
        <span className="record-label">
          <span className="ipod-image"><i className="ipod-screen">SINK</i><i className="click-wheel">◀ ▶</i></span>
          <strong>{syncing ? 'Syncing…' : connected ? 'iPod' : 'Offline'}</strong>
        </span>
      </button>
      {menuOpen && <div className="device-menu" role="menu" onMouseLeave={() => setMenuOpen(false)}>
        <div><strong>River’s iPod</strong><small>160 GB · 84 GB free</small></div>
        {syncActions.map((action) => <button role="menuitem" key={action} onClick={sync}>{action}<span>›</span></button>)}
        <button role="menuitem" onClick={() => { setConnected(false); setMenuOpen(false); }}><span>Eject</span><span>⏏</span></button>
      </div>}
    </div>
  );
}
