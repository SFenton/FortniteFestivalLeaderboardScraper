// Release builds receive FST_APP_BUILD_NUMBER (the first-parent commit count of
// master at the published commit) and FST_APP_COMMIT from the publish workflow.
// The build number replaces the package.json patch so every master release gets
// a distinct, increasing version. Builds without it keep the package.json version.

const PACKAGE_VERSION = /^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/;
const BUILD_NUMBER = /^[1-9]\d*$/;
const COMMIT = /^[0-9a-f]{7,40}$/i;

export function resolveAppVersion(packageVersion, buildNumber) {
  const parsed = PACKAGE_VERSION.exec(packageVersion ?? '');
  const build = (buildNumber ?? '').trim();
  if (!parsed || !BUILD_NUMBER.test(build)) return packageVersion;
  return `${parsed[1]}.${parsed[2]}.${build}`;
}

export function resolveAppCommit(commit) {
  const value = (commit ?? '').trim();
  return COMMIT.test(value) ? value.slice(0, 7).toLowerCase() : '';
}
