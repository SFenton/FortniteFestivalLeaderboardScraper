import { useState } from 'react';
import ChangelogModal from './ChangelogModal';

export function FocusLifecycle() {
  const [visible, setVisible] = useState(false);
  return (
    <div>
      <button type="button" onClick={() => setVisible(true)}>Open changelog</button>
      <button type="button">Background action</button>
      {visible && <ChangelogModal onDismiss={() => setVisible(false)} />}
    </div>
  );
}
