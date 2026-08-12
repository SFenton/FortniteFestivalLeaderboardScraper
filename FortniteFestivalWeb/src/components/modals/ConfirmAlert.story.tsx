import { useState } from 'react';
import ConfirmAlert from './ConfirmAlert';
import ModalShell from './components/ModalShell';

export function FocusLifecycle() {
  const [visible, setVisible] = useState(false);
  return (
    <div>
      <button type="button" onClick={() => setVisible(true)}>Open confirmation</button>
      <button type="button">Background action</button>
      {visible && (
        <ConfirmAlert
          title="Confirm component action"
          message="Choose whether to continue."
          onNo={() => setVisible(false)}
          onYes={() => setVisible(false)}
        />
      )}
    </div>
  );
}

export function NestedInModal() {
  const [parentVisible, setParentVisible] = useState(false);
  const [confirmVisible, setConfirmVisible] = useState(false);
  return (
    <div>
      <button type="button" onClick={() => setParentVisible(true)}>Open parent modal</button>
      <ModalShell
        visible={parentVisible}
        title="Parent modal"
        onClose={() => setParentVisible(false)}
      >
        <button type="button" onClick={() => setConfirmVisible(true)}>Open nested confirmation</button>
        <button type="button">Parent last action</button>
      </ModalShell>
      {confirmVisible && (
        <ConfirmAlert
          title="Nested confirmation"
          message="Confirm without escaping the parent modal."
          onNo={() => setConfirmVisible(false)}
          onYes={() => setConfirmVisible(false)}
        />
      )}
    </div>
  );
}
