export type LogBufferConfig = {
  maxLines?: number;
  maxPending?: number;
  trimPendingTo?: number;
};

export type FlushResult = {
  added: string[];
  lines: string[];
  joined: string;
};

/**
 * Pure port of the MAUI ProcessViewModel log buffering behavior:
 * - enqueue to pending (bounded)
 * - flush pending into visible lines (bounded)
 * - compute joined string on flush
 */
export class BatchedLogBuffer {
  private readonly maxLines: number;
  private readonly maxPending: number;
  private readonly trimPendingTo: number;

  private readonly pending: string[] = [];
  private readonly lines: string[] = [];

  constructor(config: LogBufferConfig = {}) {
    this.maxLines = config.maxLines ?? 4000;
    this.maxPending = config.maxPending ?? 2000;
    this.trimPendingTo = config.trimPendingTo ?? 1500;

    if (this.maxLines <= 0) throw new Error('maxLines must be > 0');
    if (this.maxPending <= 0) throw new Error('maxPending must be > 0');
    if (this.trimPendingTo <= 0) throw new Error('trimPendingTo must be > 0');
    if (this.trimPendingTo > this.maxPending) throw new Error('trimPendingTo must be <= maxPending');
  }

  enqueue(line: string): void {
    this.pending.push(line);

    if (this.pending.length > this.maxPending) {
      while (this.pending.length > this.trimPendingTo) this.pending.shift();
    }
  }

  /**
   * Flushes pending lines into the visible buffer.
   *
   * @param newline Defaults to `\n` (use `\r\n` on Windows if desired).
   */
  flush(newline = '\n'): FlushResult {
    if (this.pending.length === 0) {
      return {added: [], lines: [...this.lines], joined: this.lines.join(newline)};
    }

    const added = this.pending.splice(0, this.pending.length);
    for (const l of added) this.lines.push(l);

    while (this.lines.length > this.maxLines) this.lines.shift();

    return {added, lines: [...this.lines], joined: this.lines.join(newline)};
  }

  snapshot(): string[] {
    return [...this.lines];
  }
}
