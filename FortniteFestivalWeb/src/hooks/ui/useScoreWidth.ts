import { useMemo } from 'react';

/**
 * Computes a CSS `ch`-based width that fits the widest score in a set.
 * Used to align score columns across leaderboard sections.
 *
 * @param scores Array of numeric scores to measure.
 * @returns A CSS width string like '7ch'.
 */
export function useScoreWidth(scores: number[]): string {
  return useMemo(() => {
    let maxLen = 1;
    for (const s of scores) {
      maxLen = Math.max(maxLen, s.toLocaleString().length);
    }
    return `${maxLen}ch`;
  }, [scores]);
}

/**
 * Non-hook utility for computing score width from multiple arrays.
 * Use in components that need to compute width from heterogeneous data.
 */
export function calculateScoreWidth(...scoreSets: number[][]): string {
  let maxLen = 1;
  for (const set of scoreSets) {
    for (const s of set) {
      maxLen = Math.max(maxLen, s.toLocaleString().length);
    }
  }
  return `${maxLen}ch`;
}
