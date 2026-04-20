import { describe, expect, it } from 'vitest';
import { appStyles } from '../src/appStyles';

describe('appStyles', () => {
  it('anchors the wide quick-links rail to the shell top like the sidebar overlay', () => {
    expect(appStyles.rightRailOverlay.top).toBe(0);
    expect(appStyles.sidebarOverlay.top).toBe(0);
  });
});