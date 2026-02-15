/**
 * android:clearCacheBuildDeploy
 *
 * 1. Gradle clean
 * 2. Build debug APK
 * 3. Install APK on connected device/emulator
 *
 * Does NOT start Metro (no packager). Reuses the SUBST short-path logic
 * from run-android.js so Windows long-path issues are avoided.
 */
const {spawnSync} = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

function run(command, args, options = {}) {
  console.log(`\n> ${command} ${args.join(' ')}\n`);
  const result = spawnSync(command, args, {
    stdio: 'inherit',
    shell: true,
    ...options,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    process.exitCode = result.status ?? 1;
    throw new Error(`Command failed with exit code ${result.status}`);
  }
  return result;
}

// ── SUBST helpers (copied from run-android.js) ──────────────────────

function findExistingShortCwd(projectRoot) {
  const candidates = ['R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z'];
  for (const letter of candidates) {
    const candidateRoot = `${letter}:\\`;
    if (!fs.existsSync(candidateRoot)) continue;
    // Check if the drive root itself IS the project.
    const androidGradlew = path.join(candidateRoot, 'android', 'gradlew.bat');
    if (fs.existsSync(androidGradlew)) return candidateRoot;
    // Also check legacy: project is one level down.
    const leaf = path.basename(projectRoot);
    const candidateCwd = path.join(candidateRoot, leaf);
    const androidGradlew2 = path.join(candidateCwd, 'android', 'gradlew.bat');
    if (fs.existsSync(androidGradlew2)) return candidateCwd;
  }
  return null;
}

function pickDriveLetter() {
  const candidates = ['R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z'];
  for (const letter of candidates) {
    if (!fs.existsSync(`${letter}:\\`)) return letter;
  }
  return null;
}

function ensureSubst(driveLetter, targetPath) {
  run('cmd.exe', ['/c', 'subst', `${driveLetter}:`, '"' + targetPath + '"']);
}

// ── Resolve working directory ───────────────────────────────────────

const projectRoot = path.resolve(__dirname, '..');
let cwd = projectRoot;

if (process.platform === 'win32') {
  const existing = findExistingShortCwd(projectRoot);
  if (existing) {
    console.log(`Using existing short path: ${existing}`);
    cwd = existing;
  } else {
    const driveLetter = pickDriveLetter();
    if (!driveLetter) {
      console.error('Could not find a free drive letter for SUBST (R:-Z: all in use).');
      process.exit(1);
    }
    ensureSubst(driveLetter, projectRoot);
    cwd = `${driveLetter}:\\`;
    console.log(`Using SUBST short path: ${driveLetter}:\\ -> ${projectRoot}`);
  }
}

const androidDir = path.join(cwd, 'android');
const gradlew = process.platform === 'win32' ? 'gradlew.bat' : './gradlew';

// ── 1. Gradle clean ─────────────────────────────────────────────────
console.log('\n=== Step 1/3: Gradle clean ===');
run(gradlew, ['clean'], {cwd: androidDir});

// ── 2. Build debug APK ──────────────────────────────────────────────
console.log('\n=== Step 2/3: Build debug APK ===');
run(gradlew, ['assembleDebug'], {cwd: androidDir});

// ── 3. Install on device ────────────────────────────────────────────
console.log('\n=== Step 3/3: Install on device ===');
run('adb', ['install', '-r', '-d', path.join(androidDir, 'app', 'build', 'outputs', 'apk', 'debug', 'app-debug.apk')]);

console.log('\n✅ Clean build deployed. Start Metro separately with: yarn start');
