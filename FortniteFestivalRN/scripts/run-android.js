const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

function run(command, args, options = {}) {
  const result = spawnSync(command, args, {
    stdio: 'inherit',
    shell: true,
    ...options,
  });
  if (result.error) throw result.error;
  process.exitCode = result.status ?? 1;
  return result;
}

function pickDriveLetter() {
  // Prefer R: then S:, T:, U:, V:, W:, X:, Y:, Z:
  const candidates = ['R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z'];
  for (const letter of candidates) {
    const driveRoot = `${letter}:\\`;
    // If drive root doesn't exist, it's likely free for SUBST.
    if (!fs.existsSync(driveRoot)) return letter;
  }
  return null;
}

function findExistingShortCwd(projectRoot) {
  // If the machine already has a SUBST/mapped drive pointing at the repo root,
  // reuse it (avoids failing when R:-Z: are all already taken).
  const leaf = path.basename(projectRoot);
  const candidates = ['R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z'];

  for (const letter of candidates) {
    const candidateRoot = `${letter}:\\`;
    if (!fs.existsSync(candidateRoot)) continue;

    const candidateCwd = path.join(candidateRoot, leaf);
    const androidGradlew = path.join(candidateCwd, 'android', 'gradlew.bat');
    if (fs.existsSync(androidGradlew)) return candidateCwd;
  }

  return null;
}

function ensureSubst(driveLetter, targetPath) {
  // Create/overwrite mapping for this run.
  // Note: `subst` requires cmd; `shell: true` handles it.
  run('cmd.exe', ['/c', 'subst', `${driveLetter}:`, '"' + targetPath + '"']);
}

function clearAutolinkingCache(projectCwd) {
  // React Native's Gradle autolinking output embeds absolute paths. On Windows we
  // run builds from a SUBST drive to avoid path-length issues; if the drive
  // letter changes between runs, Gradle can reuse stale autolinking output and
  // end up referencing the old drive (resulting in "No variants exist" for many
  // native modules).
  try {
    const autolinkingDir = path.join(projectCwd, 'android', 'build', 'generated', 'autolinking');
    if (fs.existsSync(autolinkingDir)) {
      fs.rmSync(autolinkingDir, {recursive: true, force: true});
      console.log(`[run-android] Cleared autolinking cache: ${autolinkingDir}`);
    }
  } catch (e) {
    console.warn('[run-android] Failed to clear autolinking cache:', e?.message ?? String(e));
  }
}

const projectRoot = path.resolve(__dirname, '..');
const argv = process.argv.slice(2);

if (process.platform === 'win32') {
  const existingShortCwd = findExistingShortCwd(projectRoot);
  if (existingShortCwd) {
    console.log(`Using existing short path: ${existingShortCwd}`);
    clearAutolinkingCache(existingShortCwd);
    run('yarn', ['react-native', 'run-android', ...argv], { cwd: existingShortCwd });
  } else {
    const driveLetter = pickDriveLetter();
    if (!driveLetter) {
      console.error('Could not find a free drive letter for SUBST (R:-Z: all in use).');
      process.exit(1);
    }

    // Map the *parent* folder so the project isn't at the drive root (Yarn on Windows
    // can mis-handle writes to "X:" when cwd is exactly "X:\\").
    const parent = path.dirname(projectRoot);
    const leaf = path.basename(projectRoot);

    ensureSubst(driveLetter, parent);

    const shortCwd = `${driveLetter}:\\${leaf}`;
    console.log(`Using SUBST short path: ${driveLetter}:\\ -> ${parent}`);
    console.log(`Running from: ${shortCwd}`);

    clearAutolinkingCache(shortCwd);
    run('yarn', ['react-native', 'run-android', ...argv], { cwd: shortCwd });
  }
} else {
  run('yarn', ['react-native', 'run-android', ...argv], { cwd: projectRoot });
}
