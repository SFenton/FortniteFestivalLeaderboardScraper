import { Accordion } from './Accordion';

export function FocusLifecycle() {
  return (
    <div>
      <Accordion title="Advanced filters" hint="Optional controls" panelLandmark>
        <button type="button">Panel action</button>
      </Accordion>
      <button type="button">After accordion</button>
    </div>
  );
}
