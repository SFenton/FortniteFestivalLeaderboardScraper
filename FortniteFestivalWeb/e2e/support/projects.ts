export const PRIMARY_DESKTOP_PROJECT = 'chromium-desktop';
export const PRIMARY_MOBILE_PROJECT = 'chromium-mobile';
export const WIDE_PROJECT = 'chromium-wide';

export function isMobileProject(projectName: string): boolean {
  return projectName.includes('mobile');
}

export function isPrimaryDesktopProject(projectName: string): boolean {
  return projectName === PRIMARY_DESKTOP_PROJECT;
}

export function isPrimaryMobileProject(projectName: string): boolean {
  return projectName === PRIMARY_MOBILE_PROJECT;
}
