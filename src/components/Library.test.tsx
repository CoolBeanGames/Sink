import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { seedTracks } from '../data';
import { Library } from './Library';

const renderLibrary = () => render(<Library view="songs" tracks={seedTracks} playlists={[]} activePlaylistId={null} drilldown={null} onDrilldown={vi.fn()} onPlay={vi.fn()} onBack={vi.fn()} onOpenDuplicates={vi.fn()} />);

describe('Library selection', () => {
  it('selects one track with a single click and toggles with ctrl-click', () => {
    renderLibrary();
    const first = screen.getByRole('button', { name: /Neon Current/ });
    const second = screen.getByRole('button', { name: /Low Tide/ });
    fireEvent.click(first);
    expect(first).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(second, { ctrlKey: true });
    expect(screen.getByText('2 tracks selected')).toBeInTheDocument();
    fireEvent.click(first, { ctrlKey: true });
    expect(first).toHaveAttribute('aria-pressed', 'false');
  });

  it('selects ranges with shift-click', () => {
    renderLibrary();
    fireEvent.click(screen.getByRole('button', { name: /Neon Current/ }));
    fireEvent.click(screen.getByRole('button', { name: /Passenger Window/ }), { shiftKey: true });
    expect(screen.getByText('3 tracks selected')).toBeInTheDocument();
  });

  it('extends selection with shift and arrow keys', () => {
    renderLibrary();
    const first = screen.getByRole('button', { name: /Neon Current/ });
    fireEvent.click(first);
    fireEvent.keyDown(first, { key: 'ArrowDown', shiftKey: true });
    expect(screen.getByText('2 tracks selected')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Low Tide/ })).toHaveFocus();
  });
});
