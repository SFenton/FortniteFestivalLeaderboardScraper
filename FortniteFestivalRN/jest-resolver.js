/**
 * Custom Jest resolver that handles the shared @festival/core package living
 * outside the RN project root. The react-native jest resolver can't find
 * relative imports from files outside rootDir because jest-haste-map doesn't
 * include them. This resolver intercepts those cases and resolves directly
 * from the filesystem.
 */
const path = require('path');
const fs = require('fs');

// Where the shared core package lives relative to this file
const CORE_SRC = path.resolve(__dirname, '..', 'packages', 'core', 'src');
const EXTENSIONS = ['.ts', '.tsx', '.js', '.jsx', '.json'];

module.exports = (request, options) => {
  // If the requesting file is inside the shared core package and the request
  // is a relative import, resolve it directly from the filesystem.
  if (
    options.basedir &&
    request.startsWith('.')
  ) {
    const resolvedBase = path.resolve(options.basedir);
    if (resolvedBase.startsWith(CORE_SRC)) {
      const base = path.resolve(options.basedir, request);
      // Try exact path first (might have extension)
      if (fs.existsSync(base) && fs.statSync(base).isFile()) {
        return base;
      }
      // Try each extension
      for (const ext of EXTENSIONS) {
        const candidate = base + ext;
        if (fs.existsSync(candidate)) {
          return candidate;
        }
      }
      // Try index files in directory
      if (fs.existsSync(base) && fs.statSync(base).isDirectory()) {
        for (const ext of EXTENSIONS) {
          const candidate = path.join(base, 'index' + ext);
          if (fs.existsSync(candidate)) {
            return candidate;
          }
        }
      }
    }
  }

  // Fall through to the default react-native resolver
  return options.defaultResolver(request, options);
};
