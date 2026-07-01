import { useState, useEffect } from 'react';

export function useAppVersion(): string {
  const [version, setVersion] = useState<string>('');

  useEffect(() => {
    fetch('/api/version')
      .then(r => r.ok ? r.json() : null)
      .then(data => { if (data?.version) setVersion(data.version); })
      .catch(() => {}); // silent fallback
  }, []);

  return version;
}
