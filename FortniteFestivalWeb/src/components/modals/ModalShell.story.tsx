import { useState } from 'react';
import ModalShell from './components/ModalShell';

export function FocusLifecycle() {
  const [visible, setVisible] = useState(false);
  return (
    <div>
      <button type="button" onClick={() => setVisible(true)}>Open test modal</button>
      <button type="button">Background action</button>
      <ModalShell
        visible={visible}
        title="Component test modal"
        onClose={() => setVisible(false)}
      >
        <button type="button">First modal action</button>
        <button type="button">Last modal action</button>
      </ModalShell>
    </div>
  );
}
