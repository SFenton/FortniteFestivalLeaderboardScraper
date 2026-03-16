import { useState, useCallback } from 'react';

/**
 * Manages the common modal draft pattern:
 * open (copy current → draft) → edit draft → apply (draft → current) or cancel.
 *
 * @param defaults - factory function returning the default draft values
 */
export function useModalState<T>(defaults: () => T) {
  const [visible, setVisible] = useState(false);
  const [draft, setDraft] = useState<T>(defaults);

  const open = useCallback((currentValues: T) => {
    setDraft(currentValues);
    setVisible(true);
  }, []);

  const close = useCallback(() => {
    setVisible(false);
  }, []);

  const reset = useCallback(() => {
    setDraft(defaults());
  }, [defaults]);

  return { visible, draft, setDraft, open, close, reset };
}
