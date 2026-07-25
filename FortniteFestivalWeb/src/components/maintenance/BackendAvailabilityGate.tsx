import type { ReactNode } from 'react';
import { useServiceInfo } from '../../hooks/data/useServiceInfo';
import MaintenanceApp from './MaintenanceApp';

type BackendAvailabilityGateProps = {
  children: ReactNode;
};

export default function BackendAvailabilityGate({ children }: BackendAvailabilityGateProps) {
  const serviceInfo = useServiceInfo('availability');

  if (serviceInfo.isSuccess) return <>{children}</>;
  return <MaintenanceApp checking={serviceInfo.isPending} />;
}
