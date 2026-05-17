import type { CSSProperties } from 'react';
import PercentilePill from '../songs/metadata/PercentilePill';

const BASE_SIZE = 192;

const surfaceBaseStyle: CSSProperties = {
  position: 'relative',
  overflow: 'hidden',
  background: 'linear-gradient(130deg, #7C3AED 0%, #4B0F63 46%, #1A0830 100%)',
};

const shadeStyle: CSSProperties = {
  position: 'absolute',
  inset: 0,
  background: 'rgba(0, 0, 0, 0.08)',
};

const artboardStyle: CSSProperties = {
  position: 'absolute',
  inset: 0,
  width: BASE_SIZE,
  height: BASE_SIZE,
  transformOrigin: 'top left',
};

const badgeHostStyle: CSSProperties = {
  position: 'absolute',
  left: '50%',
  top: '50%',
  transform: 'translate(-50%, -50%) scale(1.08)',
  transformOrigin: 'center',
  fontFamily: 'Arial Black, Arial, sans-serif',
  fontSize: 29,
  lineHeight: 1,
  filter: 'drop-shadow(0 7px 8px rgba(0, 0, 0, 0.45))',
};

function readCaptureSize() {
  const rawSize = new URLSearchParams(window.location.search).get('pwaIconSize');
  const parsed = rawSize ? Number.parseInt(rawSize, 10) : 512;

  if (!Number.isFinite(parsed)) return 512;
  return Math.min(1024, Math.max(64, parsed));
}

export default function PwaIconCapture() {
  const size = readCaptureSize();
  const scale = size / BASE_SIZE;

  return (
    <main
      aria-label="Festival Score Tracker icon capture"
      data-testid="pwa-icon-capture"
      style={{ ...surfaceBaseStyle, width: size, height: size }}
    >
      <div style={shadeStyle} />
      <div style={{ ...artboardStyle, transform: `scale(${scale})` }}>
        <div style={badgeHostStyle}>
          <PercentilePill display="FST" tier="top1" minWidth="3em" bold />
        </div>
      </div>
    </main>
  );
}