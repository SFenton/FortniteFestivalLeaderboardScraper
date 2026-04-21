/**
 * Encapsulates the animated top-5 score card list state machine.
 *
 * On instrument change, the old list fades out, height animates, then new list fades in.
 */
import { useState, useEffect, useRef } from 'react';
import { ListPhase } from '@festival/core';
import { Size, Gap } from '@festival/theme';

const CARD_HEIGHT = Size.iconXl;
const CARD_GAP = Gap.sm;
const OUT_BASE_MS = 200;
const OUT_STEP_MS = 40;
const IN_BASE_MS = 300;
const IN_STEP_MS = 60;
const HEIGHT_TRANSITION_MS = 300;

function calcListHeight(n: number): number {
  return n > 0 ? n * CARD_HEIGHT + (n - 1) * CARD_GAP : 0;
}

function areListsEqual<T>(
  previous: T[],
  next: T[],
  isEqual: (a: T, b: T) => boolean,
): boolean {
  if (previous === next) return true;
  if (previous.length !== next.length) return false;

  for (let i = 0; i < previous.length; i += 1) {
    const prevItem = previous[i];
    const nextItem = next[i];
    if (prevItem == null || nextItem == null || !isEqual(prevItem, nextItem)) {
      return false;
    }
  }

  return true;
}

export function useListAnimation<T>(
  visibleCards: T[],
  skipAnimation?: boolean,
  isEqual: (a: T, b: T) => boolean = Object.is,
) {
  const [displayedCards, setDisplayedCards] = useState<T[]>(visibleCards);
  const [listPhase, setListPhase] = useState<ListPhase>(ListPhase.Idle);
  const [listHeight, setListHeight] = useState(() => calcListHeight(visibleCards.length));
  const listHeightRef = useRef(listHeight);
  listHeightRef.current = listHeight;
  const listTimers = useRef<ReturnType<typeof setTimeout>[]>([]);
  const prevCardsRef = useRef(visibleCards);

  useEffect(() => {
    if (areListsEqual(prevCardsRef.current, visibleCards, isEqual)) return;
    prevCardsRef.current = visibleCards;

    listTimers.current.forEach(clearTimeout);
    listTimers.current = [];

    const oldCount = displayedCards.length;
    const outDuration = oldCount > 0 ? OUT_BASE_MS + (oldCount - 1) * OUT_STEP_MS : 0;

    if (oldCount > 0) {
      const newN = visibleCards.length;
      const newHeight = calcListHeight(newN);

      if (skipAnimation) {
        setDisplayedCards(visibleCards);
        setListHeight(newHeight);
        setListPhase(ListPhase.Idle);
        return;
      }

      const isShrinking = newHeight < listHeightRef.current;

      setListPhase(ListPhase.Out);
      listTimers.current.push(setTimeout(() => {
        if (isShrinking) {
          setListHeight(newHeight);
          listTimers.current.push(setTimeout(() => {
            setDisplayedCards(visibleCards);
            setListPhase(ListPhase.In);
            const inDuration = IN_BASE_MS + (newN - 1) * IN_STEP_MS;
            listTimers.current.push(setTimeout(() => setListPhase(ListPhase.Idle), inDuration));
          }, HEIGHT_TRANSITION_MS));
        } else {
          setDisplayedCards([]);
          requestAnimationFrame(() => {
            setListHeight(newHeight);
            listTimers.current.push(setTimeout(() => {
              setDisplayedCards(visibleCards);
              setListPhase(ListPhase.In);
              const inDuration = IN_BASE_MS + (newN - 1) * IN_STEP_MS;
              listTimers.current.push(setTimeout(() => setListPhase(ListPhase.Idle), inDuration));
            }, HEIGHT_TRANSITION_MS));
          });
        }
      }, outDuration));
    } else {
      setDisplayedCards(visibleCards);
      const newN = visibleCards.length;
      setListHeight(calcListHeight(newN));
      if (skipAnimation) {
        setListPhase(ListPhase.Idle);
      } else {
        setListPhase(ListPhase.In);
        const inDuration = IN_BASE_MS + (visibleCards.length - 1) * IN_STEP_MS;
        listTimers.current.push(setTimeout(() => setListPhase(ListPhase.Idle), inDuration));
      }
    }

    return () => { listTimers.current.forEach(clearTimeout); };
  }, [visibleCards, skipAnimation, isEqual]); // eslint-disable-line react-hooks/exhaustive-deps

  return { displayedCards, listPhase, listHeight };
}
