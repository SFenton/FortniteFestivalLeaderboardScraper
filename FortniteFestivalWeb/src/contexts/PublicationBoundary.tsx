import { useEffect, useState, type ReactNode } from 'react';
import type { PublicationResponse } from '@festival/core/api';
import { queryClient } from '../api/queryClient';
import {
  activatePublicationBootstrap,
  ensurePublication,
  PUBLICATION_CHANGED_EVENT,
} from '../api/publication';
import { clearSongsCache } from '../api/songsCache';
import { resetAppWebSocketForPublicationChange } from '../hooks/data/useAppWebSocket';

export default function PublicationBoundary({
  children,
}: {
  children: ReactNode;
}) {
  const [publication, setPublication] = useState<PublicationResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    activatePublicationBootstrap();
    let active = true;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    const handleChange = (event: Event) => {
      const next = (event as CustomEvent<PublicationResponse>).detail;
      queryClient.clear();
      clearSongsCache();
      resetAppWebSocketForPublicationChange();
      if (active) setPublication(next);
    };
    const loadPublication = () => {
      void ensurePublication()
        .then(next => {
          if (!active) return;
          setError(null);
          setPublication(next);
        })
        .catch(cause => {
          if (!active) return;
          setError(cause instanceof Error ? cause.message : String(cause));
          retryTimer = setTimeout(loadPublication, 2_000);
        });
    };

    window.addEventListener(PUBLICATION_CHANGED_EVENT, handleChange);
    loadPublication();

    return () => {
      active = false;
      if (retryTimer !== undefined) clearTimeout(retryTimer);
      window.removeEventListener(PUBLICATION_CHANGED_EVENT, handleChange);
    };
  }, []);

  if (error) {
    return <div role="alert">Published data unavailable; retrying: {error}</div>;
  }
  if (!publication) {
    return <div aria-busy="true">Loading published data...</div>;
  }

  return <div key={publication.publicationId}>{children}</div>;
}
