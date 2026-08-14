import { Gap, Layout } from '@festival/theme';

export interface DockLabelLayout {
  showLabels: boolean;
  searchWidth: number;
  searchTargetWidth: number;
  actionWidths: number[];
}

export const DEFAULT_DOCK_LABEL_LAYOUT: DockLabelLayout = {
  showLabels: false,
  searchWidth: Layout.fabSize,
  searchTargetWidth: Layout.fabSize,
  actionWidths: [],
};

export function calculateDockLabelLayout({
  stageWidth,
  measuredWidths,
  actionHasAccessory,
  hasMainFab,
}: {
  stageWidth: number;
  measuredWidths: readonly number[];
  actionHasAccessory: readonly boolean[];
  hasMainFab: boolean;
}): DockLabelLayout {
  const searchWidth = Math.max(
    Layout.fabSize,
    measuredWidths[0] ?? Layout.fabSize,
  );
  const actionWidths = actionHasAccessory.map((hasAccessory, index) => (
    hasAccessory
      ? Math.max(
          Layout.fabSize,
          measuredWidths[index + 1] ?? Layout.fabSize,
        )
      : Layout.fabSize
  ));
  const visibleControlCount = 1 + actionWidths.length;
  const availableSearchWidth = stageWidth
    - actionWidths.reduce((total, width) => total + width, 0)
    - (hasMainFab ? Layout.fabSize : 0)
    - (Gap.sm * visibleControlCount);

  return {
    showLabels: availableSearchWidth >= searchWidth,
    searchWidth,
    searchTargetWidth: Math.max(Layout.fabSize, availableSearchWidth),
    actionWidths,
  };
}

export function areDockLabelLayoutsEqual(
  left: DockLabelLayout,
  right: DockLabelLayout,
): boolean {
  return left.showLabels === right.showLabels
    && left.searchWidth === right.searchWidth
    && left.searchTargetWidth === right.searchTargetWidth
    && left.actionWidths.length === right.actionWidths.length
    && left.actionWidths.every(
      (width, index) => width === right.actionWidths[index],
    );
}
