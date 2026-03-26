/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Pinch-to-zoom image component with double-click and ctrl+wheel support.
 * Extracted from PathsModal for independent testability.
 */
import { forwardRef, memo, useRef, useState, useEffect, useCallback } from 'react';
import { Radius, TRANSITION_MS } from '@festival/theme';

const FADE_MS = TRANSITION_MS;

export const ZoomableImage = memo(forwardRef<HTMLImageElement, { src: string; alt: string; visible: boolean }>(
  function ZoomableImage({ src, alt, visible }, fwdRef) {
    const containerRef = useRef<HTMLDivElement>(null);
    const innerRef = useRef<HTMLImageElement>(null);
    const [scale, setScale] = useState(1);
    const [translate, setTranslate] = useState({ x: 0, y: 0 });
    const gestureRef = useRef({ startScale: 1, startDist: 0, startX: 0, startY: 0, startTx: 0, startTy: 0 });

    useEffect(() => {
      if (typeof fwdRef === 'function') fwdRef(innerRef.current);
      else if (fwdRef) (fwdRef as React.MutableRefObject<HTMLImageElement | null>).current = innerRef.current;
    });

    useEffect(() => {
      setScale(1);
      setTranslate({ x: 0, y: 0 });
    }, [src]);

    const getTouchDist = (t: React.TouchList) => {
      const dx = t[1]!.clientX - t[0]!.clientX;
      const dy = t[1]!.clientY - t[0]!.clientY;
      return Math.sqrt(dx * dx + dy * dy);
    };

    const getTouchCenter = (t: React.TouchList) => ({
      x: (t[0]!.clientX + t[1]!.clientX) / 2,
      y: (t[0]!.clientY + t[1]!.clientY) / 2,
    });

    const handleTouchStart = useCallback((e: React.TouchEvent) => {
      if (e.touches.length === 2) {
        e.preventDefault();
        gestureRef.current = {
          startScale: scale,
          startDist: getTouchDist(e.touches),
          startX: getTouchCenter(e.touches).x,
          startY: getTouchCenter(e.touches).y,
          startTx: translate.x,
          startTy: translate.y,
        };
      }
    }, [scale, translate]);

    const handleTouchMove = useCallback((e: React.TouchEvent) => {
      if (e.touches.length === 2) {
        e.preventDefault();
        const g = gestureRef.current;
        const dist = getTouchDist(e.touches);
        const center = getTouchCenter(e.touches);
        const newScale = Math.min(Math.max(g.startScale * (dist / g.startDist), 1), 5);
        const dx = center.x - g.startX;
        const dy = center.y - g.startY;
        setScale(newScale);
        setTranslate({ x: g.startTx + dx, y: g.startTy + dy });
      }
    }, []);

    const handleDoubleClick = useCallback(() => {
      if (scale > 1) {
        setScale(1);
        setTranslate({ x: 0, y: 0 });
      } else {
        setScale(2.5);
      }
    }, [scale]);

    const handleWheel = useCallback((e: React.WheelEvent) => {
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault();
        const delta = e.deltaY > 0 ? 0.9 : 1.1;
        setScale(s => Math.min(Math.max(s * delta, 1), 5));
      }
    }, []);

    const transform = `translate(${translate.x}px, ${translate.y}px) scale(${scale})`;

    return (
      <div
        ref={containerRef}
        onTouchStart={handleTouchStart}
        onTouchMove={handleTouchMove}
        onDoubleClick={handleDoubleClick}
        onWheel={handleWheel}
        style={{ touchAction: scale > 1 ? 'none' : 'pan-y', display: 'inline-block' }}
      >
        <img
          ref={innerRef}
          src={src}
          alt={alt}
          draggable={false}
          style={{
            maxWidth: '100%',
            height: 'auto',
            borderRadius: Radius.md,
            userSelect: 'none',
            opacity: visible ? 1 : 0,
            transform: visible ? transform : `translateY(16px) ${transform}`,
            transformOrigin: 'center top',
            transition: scale === 1 && translate.x === 0 && translate.y === 0
              ? `opacity ${FADE_MS}ms ease, transform ${FADE_MS}ms ease`
              : `opacity ${FADE_MS}ms ease`,
          }}
        />
      </div>
    );
  },
));
