import {Image} from 'react-native';

import type {Song} from '../core/models';
import type {ImageCache} from '../core/services/types';

const getArtworkUri = (song: Song): string | undefined => {
  const uri = song.imagePath ?? song.track.au;
  if (!uri) return undefined;
  const trimmed = uri.trim();
  return trimmed.length ? trimmed : undefined;
};

export const createNativeImageCache = (): ImageCache => {
  return {
    async ensureCached(song: Song): Promise<string | undefined> {
      const uri = getArtworkUri(song);
      if (!uri) return undefined;

      // Avoid doing work in unit tests, and stay resilient if RNW doesn't implement Image.prefetch.
      if (!process.env.JEST_WORKER_ID) {
        try {
          const prefetch = (Image as any)?.prefetch as undefined | ((u: string) => Promise<boolean>);
          if (typeof prefetch === 'function') {
            await prefetch(uri);
          }
        } catch {
          // swallow: image caching is best-effort
        }
      }

      // Note: we currently persist the artwork URI (not a file path). That still enables
      // prefetching + display via <Image source={{uri}} /> without extra native FS deps.
      return uri;
    },
  };
};
