import type {Song} from '../models';

// Matches the C# logic: iterate root object properties; treat each object value as a Song-like payload.
export const parseSongCatalog = (content: string | null | undefined): Song[] => {
  if (!content) return [];
  const trimmed = content.trim();
  if (!trimmed.startsWith('{')) return [];

  try {
    const root = JSON.parse(trimmed) as Record<string, unknown>;
    const out: Song[] = [];
    for (const value of Object.values(root)) {
      if (!value || typeof value !== 'object') continue;
      // quick filter to avoid expensive validation
      const raw = JSON.stringify(value);
      if (!raw.includes('"su"')) continue;
      const song = value as Song;
      if (song?.track?.su) out.push(song);
    }
    return out;
  } catch {
    return [];
  }
};
