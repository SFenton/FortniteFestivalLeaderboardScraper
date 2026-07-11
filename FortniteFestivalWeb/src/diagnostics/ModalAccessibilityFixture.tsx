import { useEffect, useRef, useState } from 'react';
import ModalShell from '../components/modals/components/ModalShell';

export default function ModalAccessibilityFixture() {
  const [visible, setVisible] = useState(false);
  const launcherRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    launcherRef.current?.focus();
  }, []);

  return (
    <main>
      <h1>Modal accessibility fixture</h1>
      <button ref={launcherRef} type="button" onClick={() => setVisible(true)}>
        Launch accessible modal
      </button>
      <button type="button">Background action</button>
      <ModalShell visible={visible} title="Accessibility test modal" onClose={() => setVisible(false)}>
        <div>
          <p>Keyboard focus must remain inside this dialog.</p>
          <button type="button">First modal action</button>
          <button type="button">Last modal action</button>
        </div>
      </ModalShell>
    </main>
  );
}
