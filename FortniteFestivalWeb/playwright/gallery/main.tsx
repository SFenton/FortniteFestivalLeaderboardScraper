import { StrictMode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import '../../src/i18n';
import '../../src/index.css';

type StoryComponent = (props?: Record<string, unknown>) => React.ReactNode;
type StoryModule = Record<string, StoryComponent>;
type MountParams = {
  story: string;
  props?: Record<string, unknown>;
};

const storyModules = import.meta.glob<StoryModule>('../../src/**/*.story.tsx');
let root: Root | null = null;

declare global {
  interface Window {
    mount(params: MountParams): Promise<void>;
    unmount(): Promise<void>;
  }
}

window.mount = async ({ story, props }) => {
  const { modulePath, exportName } = resolveStory(story);
  const loader = storyModules[modulePath];
  if (!loader) throw new Error(`Unknown component story module: ${modulePath}`);
  const storyModule = await loader();
  const Story = storyModule[exportName];
  if (typeof Story !== 'function') {
    throw new Error(`Unknown component story export: ${story}`);
  }

  root ??= createRoot(document.getElementById('root')!);
  root.render(
    <StrictMode>
      <Story {...props} />
    </StrictMode>,
  );
};

window.unmount = async () => {
  root?.unmount();
  root = null;
  document.getElementById('root')!.replaceChildren();
};

function resolveStory(story: string): {
  modulePath: string;
  exportName: string;
} {
  const separator = story.lastIndexOf('/');
  if (separator <= 0 || separator === story.length - 1) {
    throw new Error(`Invalid component story id: ${story}`);
  }
  return {
    modulePath: `../../src/${story.slice(0, separator)}.story.tsx`,
    exportName: story.slice(separator + 1),
  };
}
