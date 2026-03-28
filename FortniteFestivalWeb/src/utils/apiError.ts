import i18next from 'i18next';

export interface ApiErrorInfo {
  title: string;
  subtitle: string;
}

const API_ERROR_RE = /^(?:Error:\s*)?API (\d{3}):/;

function categoryForStatus(status: number): string {
  if (status === 404) return 'notFound';
  if (status >= 400 && status < 500) return 'clientError';
  if (status === 500) return 'serverError';
  if (status >= 502 && status <= 504) return 'serviceUnavailable';
  return 'unknown';
}

export function parseApiError(error: string): ApiErrorInfo {
  const match = API_ERROR_RE.exec(error);
  const category = match ? categoryForStatus(Number(match[1])) : 'unknown';
  return {
    title: i18next.t(`apiError.${category}`),
    subtitle: i18next.t(`apiError.${category}Subtitle`),
  };
}
