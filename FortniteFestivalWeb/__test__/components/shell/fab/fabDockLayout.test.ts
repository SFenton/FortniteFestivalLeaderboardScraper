import { describe, expect, it } from 'vitest';
import {
  DEFAULT_DOCK_LABEL_LAYOUT,
  areDockLabelLayoutsEqual,
  calculateDockLabelLayout,
} from '../../../../src/components/shell/fab/fabDockLayout';

describe('fabDockLayout', () => {
  it('shows labels only when measured controls fit', () => {
    expect(calculateDockLabelLayout({
      stageWidth: 400,
      measuredWidths: [180, 100],
      actionHasAccessory: [true],
      hasMainFab: true,
    })).toEqual({
      showLabels: true,
      searchWidth: 180,
      searchTargetWidth: 236,
      actionWidths: [100],
    });
  });

  it('keeps accessory-free actions circular and clamps search width', () => {
    expect(calculateDockLabelLayout({
      stageWidth: 170,
      measuredWidths: [200, 120],
      actionHasAccessory: [false],
      hasMainFab: true,
    })).toEqual({
      showLabels: false,
      searchWidth: 200,
      searchTargetWidth: 56,
      actionWidths: [56],
    });
  });

  it('compares layouts structurally', () => {
    expect(areDockLabelLayoutsEqual(
      DEFAULT_DOCK_LABEL_LAYOUT,
      { ...DEFAULT_DOCK_LABEL_LAYOUT, actionWidths: [] },
    )).toBe(true);
    expect(areDockLabelLayoutsEqual(
      DEFAULT_DOCK_LABEL_LAYOUT,
      { ...DEFAULT_DOCK_LABEL_LAYOUT, showLabels: true },
    )).toBe(false);
  });
});
