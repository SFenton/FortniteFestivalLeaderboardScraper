import type { ServerSong as Song } from '@festival/core/api/serverTypes';

const DIACRITIC_RE = /[\u0300-\u036f]/g;
const APOSTROPHE_RE = /['‘’`´]/g;
const SEARCH_SEPARATOR_RE = /[\s()[\]{}"“”.,:;!?_\-–—/\\]+/g;

function normalizeSongSearchText(value: string): string {
  return value
    .normalize('NFKD')
    .replace(DIACRITIC_RE, '')
    .toLowerCase()
    .replace(APOSTROPHE_RE, '')
    .replace(SEARCH_SEPARATOR_RE, ' ')
    .trim()
    .replace(/\s+/g, ' ');
}

export function songMatchesSearch(song: Song, search: string): boolean {
  const rawQuery = search.trim().toLowerCase();
  if (!rawQuery) return true;

  const rawTitle = song.title.toLowerCase();
  const rawArtist = song.artist.toLowerCase();
  if (rawTitle.includes(rawQuery) || rawArtist.includes(rawQuery)) return true;

  const normalizedQuery = normalizeSongSearchText(search);
  if (!normalizedQuery) return true;

  return normalizeSongSearchText(song.title).includes(normalizedQuery)
    || normalizeSongSearchText(song.artist).includes(normalizedQuery);
}