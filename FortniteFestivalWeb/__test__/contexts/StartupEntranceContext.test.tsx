import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import {
  StartupEntranceProvider,
  useStartupEntranceComplete,
} from '../../src/contexts/StartupEntranceContext';

function Probe() {
  return <output>{String(useStartupEntranceComplete())}</output>;
}

describe('StartupEntranceContext', () => {
  it('defaults to complete for independently rendered descendants', () => {
    render(<Probe />);
    expect(screen.getByText('true')).toBeInTheDocument();
  });

  it('publishes the app-owned entrance state', () => {
    render(
      <StartupEntranceProvider complete={false}>
        <Probe />
      </StartupEntranceProvider>,
    );
    expect(screen.getByText('false')).toBeInTheDocument();
  });
});
