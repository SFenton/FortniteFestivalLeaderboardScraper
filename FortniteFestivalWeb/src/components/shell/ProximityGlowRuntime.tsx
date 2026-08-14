import { useProximityGlow } from '../../hooks/ui/useProximityGlow';

export default function ProximityGlowRuntime({ enabled }: { enabled: boolean }) {
  useProximityGlow(enabled);
  return null;
}
