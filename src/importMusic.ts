import type { Track } from './types';

const musicExtensions = new Set(['mp3', 'm4a', 'aac', 'wav', 'flac', 'ogg', 'opus']);
const isMusic = (file: File) => file.type.startsWith('audio/') || musicExtensions.has(file.name.split('.').pop()?.toLowerCase() ?? '');

const readFile = (entry: FileSystemFileEntry) => new Promise<File>((resolve, reject) => entry.file(resolve, reject));

async function readDirectory(entry: FileSystemDirectoryEntry): Promise<File[]> {
  const reader = entry.createReader();
  const children: FileSystemEntry[] = [];
  while (true) {
    const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
    if (!batch.length) break;
    children.push(...batch);
  }
  return (await Promise.all(children.map(readEntry))).flat();
}

async function readEntry(entry: FileSystemEntry): Promise<File[]> {
  if (entry.isFile) return [await readFile(entry as FileSystemFileEntry)];
  if (entry.isDirectory) return readDirectory(entry as FileSystemDirectoryEntry);
  return [];
}

export async function filesFromDrop(transfer: DataTransfer): Promise<File[]> {
  const entries = [...transfer.items].map((item) => item.webkitGetAsEntry()).filter((entry): entry is FileSystemEntry => entry !== null);
  const files = entries.length ? (await Promise.all(entries.map(readEntry))).flat() : [...transfer.files];
  return files.filter(isMusic);
}

export function trackFromFile(file: File): Track {
  const basename = file.name.replace(/\.[^.]+$/, '');
  const cleanName = basename.replace(/^\d+[\s._-]*/, '').replace(/[_-]+/g, ' ').trim();
  return {
    id: crypto.randomUUID(),
    title: cleanName || basename,
    artist: 'Unknown Artist',
    album: 'Imported Music',
    genre: 'Unknown',
    duration: 0,
    year: new Date(file.lastModified).getFullYear(),
    trackNumber: 0,
    fileName: file.name,
    source: URL.createObjectURL(file),
  };
}
