import {Platform} from 'react-native';
import RNFS from 'react-native-fs';

import type {Song} from '@festival/core';
import type {ImageCache} from '@festival/core';

const IMAGE_CACHE_DIR = `${RNFS.CachesDirectoryPath}/fnfestival_images`;

/**
 * Derive a stable, filesystem-safe filename from a song's artwork URL.
 * Uses the song ID + a hash-like suffix from the URL to avoid collisions.
 */
function localFilename(song: Song): string | undefined {
  const url = song.track.au;
  if (!url) return undefined;

  // Use song ID as the base; append the last path segment of the URL for uniqueness.
  const songId = song.track.su;
  const lastSegment = url.split('/').pop()?.split('?')[0] ?? '';
  const ext = lastSegment.includes('.') ? lastSegment.substring(lastSegment.lastIndexOf('.')) : '.jpg';
  return `${songId}${ext}`;
}

async function ensureDir(): Promise<void> {
  const exists = await RNFS.exists(IMAGE_CACHE_DIR);
  if (!exists) {
    await RNFS.mkdir(IMAGE_CACHE_DIR);
  }
}

export const createNativeImageCache = (): ImageCache => {
  return {
    async ensureCached(song: Song): Promise<string | undefined> {
      const remoteUrl = song.track.au;
      if (!remoteUrl?.trim()) return undefined;

      const filename = localFilename(song);
      if (!filename) return undefined;

      const localPath = `${IMAGE_CACHE_DIR}/${filename}`;

      // If already on disk, return immediately (offline-friendly).
      const exists = await RNFS.exists(localPath);
      if (exists) {
        return Platform.OS === 'android' ? `file://${localPath}` : localPath;
      }

      // Download to disk.
      try {
        await ensureDir();
        const result = await RNFS.downloadFile({
          fromUrl: remoteUrl,
          toFile: localPath,
        }).promise;

        if (result.statusCode >= 200 && result.statusCode < 300) {
          return Platform.OS === 'android' ? `file://${localPath}` : localPath;
        }

        // Non-success status: clean up partial file
        const partialExists = await RNFS.exists(localPath);
        if (partialExists) await RNFS.unlink(localPath);
      } catch {
        // Download failed (e.g. offline). Clean up partial file if any.
        try {
          const partialExists = await RNFS.exists(localPath);
          if (partialExists) await RNFS.unlink(localPath);
        } catch {
          // swallow
        }
      }

      return undefined;
    },

    async clearAll(): Promise<void> {
      try {
        const exists = await RNFS.exists(IMAGE_CACHE_DIR);
        if (exists) {
          await RNFS.unlink(IMAGE_CACHE_DIR);
        }
      } catch {
        // swallow
      }
    },
  };
};
