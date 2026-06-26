import { useEffect, useState, type ReactNode } from 'react';
import MaintenanceApp from './MaintenanceApp';

const CHECK_TIMEOUT_MS = 3_000;
const AVAILABLE_POLL_INTERVAL_MS = 30_000;
const UNAVAILABLE_RETRY_INTERVAL_MS = 5_000;

type BackendAvailability = 'checking' | 'available' | 'unavailable';

type ServiceInfoResponse = {
  workerStatus?: {
    status?: string | null;
  } | null;
};

type BackendAvailabilityGateProps = {
  children: ReactNode;
};

export default function BackendAvailabilityGate({ children }: BackendAvailabilityGateProps) {
  const [availability, setAvailability] = useState<BackendAvailability>('checking');

  useEffect(() => {
    let cancelled = false;
    let retryTimer: number | undefined;
    let timeoutTimer: number | undefined;

    const check = async () => {
      const controller = new AbortController();
      timeoutTimer = window.setTimeout(() => controller.abort(), CHECK_TIMEOUT_MS);

      const available = await isBackendAvailable(controller.signal);
      window.clearTimeout(timeoutTimer);

      if (cancelled) return;

      setAvailability(available ? 'available' : 'unavailable');
      retryTimer = window.setTimeout(
        check,
        available ? AVAILABLE_POLL_INTERVAL_MS : UNAVAILABLE_RETRY_INTERVAL_MS,
      );
    };

    void check();

    return () => {
      cancelled = true;
      window.clearTimeout(retryTimer);
      window.clearTimeout(timeoutTimer);
    };
  }, []);

  if (availability === 'available') return <>{children}</>;
  return <MaintenanceApp checking={availability === 'checking'} />;
}

async function isBackendAvailable(signal: AbortSignal): Promise<boolean> {
  try {
    const response = await fetch('/api/service-info', {
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      signal,
    });

    if (!response.ok) return false;

    const serviceInfo = await response.json() as ServiceInfoResponse;
    return serviceInfo.workerStatus?.status?.toLowerCase() === 'online';
  } catch {
    return false;
  }
}
