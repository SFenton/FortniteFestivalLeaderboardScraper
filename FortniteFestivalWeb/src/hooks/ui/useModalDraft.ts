import { useState, useCallback, useMemo } from 'react';

/**
 * Manages the "unsaved changes" confirm-discard flow used by modal dialogs.
 *
 * Replaces the duplicated pattern in SortModal, FilterModal,
 * PlayerScoreSortModal, SuggestionsFilterModal:
 *
 *   const hasChanges = useMemo(() => JSON.stringify(draft) !== JSON.stringify(savedDraft), ...);
 *   const [confirmOpen, setConfirmOpen] = useState(false);
 *   const handleClose = () => hasChanges ? setConfirmOpen(true) : onCancel();
 *   const confirmDiscard = () => { setConfirmOpen(false); onCancel(); };
 *
 * @param draft      Current draft state
 * @param savedDraft Last-applied state (or initial state)
 * @param onCancel   Callback when the modal should close without applying
 * @param isEqual    Optional custom equality check. Default: JSON.stringify comparison.
 */
export function useModalDraft<T>(
  draft: T,
  savedDraft: T | undefined,
  onCancel: () => void,
  isEqual?: (a: T, b: T) => boolean,
) {
  const hasChanges = useMemo(() => {
    if (!savedDraft) return true;
    if (isEqual) return !isEqual(draft, savedDraft);
    return JSON.stringify(draft) !== JSON.stringify(savedDraft);
  }, [draft, savedDraft, isEqual]);

  const [confirmOpen, setConfirmOpen] = useState(false);

  const handleClose = useCallback(() => {
    if (hasChanges) {
      setConfirmOpen(true);
    } else {
      onCancel();
    }
  }, [hasChanges, onCancel]);

  const confirmDiscard = useCallback(() => {
    setConfirmOpen(false);
    onCancel();
  }, [onCancel]);

  return { hasChanges, confirmOpen, setConfirmOpen, handleClose, confirmDiscard };
}
