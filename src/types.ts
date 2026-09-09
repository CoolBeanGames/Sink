export type Track = {
  id: string;
  title: string;
  artist: string;
  album: string;
  genre: string;
  duration: number;
  year: number;
  trackNumber: number;
  fileName: string;
  source?: string;
  artwork?: string;
};

export type Playlist = {
  id: string;
  name: string;
  trackIds: string[];
};

export type LibraryView = 'artists' | 'albums' | 'genres' | 'songs' | 'playlist';
