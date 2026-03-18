import type {Settings} from '@festival/core';
import {defaultSettings} from '@festival/core';
import {savePretty} from '../../io/jsonSerializer';
import type {FileStore} from './fileStore.types';

export class JsonSettingsPersistence {
  constructor(private readonly store: FileStore, private readonly path: string) {}

  async loadSettings(): Promise<Settings> {
    try {
      const exists = await this.store.exists(this.path);
      if (!exists) return defaultSettings();
      const json = await this.store.readText(this.path);
      const parsed = JSON.parse(json) as Partial<Settings>;
      return {...defaultSettings(), ...parsed};
    } catch {
      return defaultSettings();
    }
  }

  async saveSettings(settings: Settings): Promise<void> {
    try {
      const json = savePretty(settings);
      if (!json) return;
      await this.store.writeText(this.path, json);
    } catch {
      // swallow
    }
  }
}
