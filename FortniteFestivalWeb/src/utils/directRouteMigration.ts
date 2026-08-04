export function migrateDirectPathToHashRoute(windowRef: Window = window): void {
  const { pathname, search, hash } = windowRef.location;
  if (hash || pathname === '/' || pathname === '/index.html') return;

  windowRef.history.replaceState(
    windowRef.history.state,
    '',
    `/#${pathname}${search}`,
  );
}
