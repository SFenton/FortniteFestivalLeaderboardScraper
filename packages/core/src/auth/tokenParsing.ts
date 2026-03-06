import type {ExchangeCodeToken} from './exchangeCode.types';

export const parseExchangeCodeToken = (body: string | null | undefined): ExchangeCodeToken | null => {
  if (!body) return null;
  const trimmed = body.trim();
  if (!trimmed.startsWith('{')) return null;
  try {
    const obj = JSON.parse(trimmed) as Record<string, unknown>;
    if (typeof obj.errorCode === 'string') return null;
    const access_token = typeof obj.access_token === 'string' ? obj.access_token : '';
    const account_id = typeof obj.account_id === 'string' ? obj.account_id : '';
    if (!access_token || !account_id) return null;
    return obj as unknown as ExchangeCodeToken;
  } catch {
    return null;
  }
};

export const parseTokenVerify = (body: string | null | undefined): {accountId?: string; displayName?: string} => {
  if (!body) return {};
  const trimmed = body.trim();
  if (!trimmed.startsWith('{')) return {};
  try {
    const obj = JSON.parse(trimmed) as Record<string, unknown>;
    const accountId = typeof obj.account_id === 'string' ? obj.account_id : undefined;
    const displayName = typeof obj.displayName === 'string' ? obj.displayName : undefined;
    return {accountId, displayName};
  } catch {
    return {};
  }
};
