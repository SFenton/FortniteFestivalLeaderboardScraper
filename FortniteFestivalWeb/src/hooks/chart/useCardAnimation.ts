/**
 * Encapsulates the sequenced card animation state machine for ScoreHistoryChart.
 *
 * Phases: Closed → Growing → Open, swapping: Open → SwapOut → SwapIn → Open,
 * closing: Open → Fading → Shrinking → Closed.
 */
import { useState, useEffect, useRef } from 'react';
import { CardPhase } from '@festival/core';

export function useCardAnimation<T>(selectedPoint: T | null) {
  const [displayedPoint, setDisplayedPoint] = useState<T | null>(null);
  const [cardPhase, setCardPhase] = useState<CardPhase>(CardPhase.Closed);
  const [cardHeight, setCardHeight] = useState(0);
  const cardContentRef = useRef<HTMLDivElement>(null);

  const cardPhaseRef = useRef(cardPhase);
  cardPhaseRef.current = cardPhase;
  const displayedPointRef = useRef(displayedPoint);
  displayedPointRef.current = displayedPoint;
  const cardTimers = useRef<ReturnType<typeof setTimeout>[]>([]);
  const pendingPoint = useRef<T | null>(null);

  useEffect(() => {
    cardTimers.current.forEach(clearTimeout);
    cardTimers.current = [];
    const phase = cardPhaseRef.current;
    const shown = displayedPointRef.current;

    if (selectedPoint) {
      // Swap: card already open, switching to different point
      if (shown && (phase === CardPhase.Open || phase === CardPhase.SwapIn || phase === CardPhase.SwapOut)) {
        pendingPoint.current = selectedPoint;
        setCardPhase(CardPhase.SwapOut);
        cardTimers.current.push(setTimeout(() => {
          setDisplayedPoint(pendingPoint.current);
          pendingPoint.current = null;
          setCardPhase(CardPhase.SwapIn);
          cardTimers.current.push(setTimeout(() => setCardPhase(CardPhase.Open), 150));
        }, 150));
      } else {
        // Opening from closed
        setDisplayedPoint(selectedPoint);
        requestAnimationFrame(() => {
          if (cardContentRef.current) {
            setCardHeight(cardContentRef.current.offsetHeight + 2);
          }
          setCardPhase(CardPhase.Growing);
          cardTimers.current.push(setTimeout(() => setCardPhase(CardPhase.Open), 250));
        });
      }
    } else if (shown && phase !== CardPhase.Closed) {
      setCardPhase(CardPhase.Fading);
      cardTimers.current.push(setTimeout(() => {
        setCardPhase(CardPhase.Shrinking);
        cardTimers.current.push(setTimeout(() => {
          setDisplayedPoint(null);
          setCardPhase(CardPhase.Closed);
        }, 250));
      }, 200));
    }

    return () => { cardTimers.current.forEach(clearTimeout); };
  }, [selectedPoint]);

  return { displayedPoint, cardPhase, cardHeight, cardContentRef };
}
