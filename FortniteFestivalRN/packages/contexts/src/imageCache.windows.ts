/**
 * imageCache – Windows variant.
 *
 * react-native-fs has no Windows native module, so we skip filesystem caching
 * entirely.  Instead, ensureCached() returns the remote URL directly — images
 * load from the network on every launch, which is acceptable for the Windows
 * desktop target.
 *
 * Metro automatically picks this file over imageCache.ts when bundling for
 * platform === 'windows'.
 */
import type {Song} from '@festival/core';
import type {ImageCache} from '@festival/core';

export const createNativeImageCache = (): ImageCache => {
  return {
    async ensureCached(song: Song): Promise<string | undefined> {
      // Return the remote album-art URL directly (no local caching).
      const remoteUrl = song.track.au;
      return remoteUrl?.trim() ? remoteUrl : undefined;
    },

    async clearAll(): Promise<void> {
      // No local cache to clear on Windows.
    },
  };
};
