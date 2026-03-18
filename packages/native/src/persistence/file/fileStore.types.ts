export interface FileStore {
  exists(path: string): Promise<boolean>;
  readText(path: string): Promise<string>;
  writeText(path: string, content: string): Promise<void>;
}

export class InMemoryFileStore implements FileStore {
  private readonly files = new Map<string, string>();

  async exists(path: string): Promise<boolean> {
    return this.files.has(path);
  }

  async readText(path: string): Promise<string> {
    const v = this.files.get(path);
    if (v == null) throw new Error('file not found');
    return v;
  }

  async writeText(path: string, content: string): Promise<void> {
    this.files.set(path, content);
  }
}
