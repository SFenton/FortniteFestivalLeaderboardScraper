export const loadOrDefault = <T>(json: string | null | undefined, factory: () => T): T => {
  if (!json) return factory();
  try {
    const obj = JSON.parse(json) as T | null;
    return obj == null ? factory() : obj;
  } catch {
    return factory();
  }
};

export const parseJson = (json: string): unknown => {
  return JSON.parse(json) as unknown;
};

export const savePretty = (obj: unknown): string => {
  try {
    return JSON.stringify(obj, null, 2);
  } catch {
    return '';
  }
};
