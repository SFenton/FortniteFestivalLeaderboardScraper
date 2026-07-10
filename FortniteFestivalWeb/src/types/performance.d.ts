interface Window {
  __fstLongTasks?: Array<{ startTime: number; duration: number }>;
}

interface Performance {
  memory?: {
    usedJSHeapSize: number;
  };
}
