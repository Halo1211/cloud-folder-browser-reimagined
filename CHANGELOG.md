# Changelog

All notable changes to Cloud Folder Browser Reimagined are documented here.

## [1.0.4] - 2026-10-14

### Fixed

- Encrypt and scope portable AllSync credentials, pass authentication context
  to downloads, and prevent reuse of a previous share's credentials.
- Handle absolute WebDAV hrefs, distinct folder case, and empty AllSync shares.
- Distinguish rounded web-index sizes from exact lengths and preserve large
  integer byte counts and literal percent characters in generated paths.
- Recover stale unknown-size partial downloads after HTTP 416, accept chunked
  downloads with estimated sizes, and preserve remote modification times.
- Reject unsafe explicit Windows targets and existing download-path reparse
  points, and validate saved-folder metadata before replacing the current tree.
- Keep search callbacks consistent with control lifetime and the latest edit;
  display cloud/local names containing `+` and `%` literally.

### Verification

- 469 passing tests, including 44 additional regression cases.
- Release build with warnings treated as errors.

### Changed

- Update GitHub Actions to Node.js 24 versions and reject release tags that do
  not match the application version.
- Preserve local portable settings when rebuilding release packages.
- Simplify the English documentation and clarify supported sources and limits.

## [1.0.3] - 2026-10-01

### Fixed

- Preserve existing files during failed overwrites and validate HTTP resume,
  byte ranges, remote checksums, and content encoding before completion.
- Prevent unsafe reuse of saved segments without a remote version validator,
  cancel sibling workers promptly, and reject transfer target/artifact collisions.
- Keep queued jobs and completed files consistent through pause, retry, and
  cancellation, including cancellation after optional post-processing starts.
- Restrict source credentials to their origin and apply cookie domain, path,
  and Secure rules through redirects and refreshed browser sessions.
- Correct WebDAV parsing, requested subfolder paths, Unicode authentication,
  AllSync/Qloud decoding, FogLink trees, and unknown-size metadata.
- Correct sync preview rename/reset behavior, selection preservation, sorting,
  and size totals; maintain one active account per provider.
- Make history, checksum, account, and folder-cache writes atomic and retain
  usable state when writes or saved-data loading fail.
- Validate saved folder types, MEGA sessions, retry entries, DNS responses,
  cache TTLs, network settings, and encrypted backup contents.
- Protect ZIP extraction from unsafe Windows paths and junctions, stop external
  processes on cancellation, and select matching PAR2 recovery files.
- Preserve JDownloader URL encoding and user data during export failures.

### Changed

- Encrypt and scope saved share passwords while keeping encrypted backups
  portable across Windows users.
- Expand the automated regression suite from 150 to 425 tests.
- Exclude local and generated data from source archives.

### Verification

- Release solution build: zero warnings and zero errors.
- Full regression suite: 425 passing tests, zero failures, and zero skipped tests.
- NuGet advisory check: no vulnerable packages reported.
- UI snapshots inspected for the main window, sync preview, and download history.

Live OAuth/cloud providers and optional external tools were not all tested
end-to-end. See [testing and known limits](docs/testing.md) for validation scope.

## [1.0.2] - 2026-09-23

### Fixed

- Corrected MEGA shared-folder node decryption when a node carries multiple encrypted keys, preventing deserialization errors from appearing as file names.
- Preserved shared keys across child nodes and recognized the selected share root correctly.
- Fixed MEGA folder and file link parsing, including root folders and links to selected children.

## [1.0.1] - 2026-09-20

### Fixed

- Prevented the final download-behavior option from being clipped at higher Windows display scaling settings.

## [1.0.0] - 2026-09-01

### Added

- Encrypted multi-account management for cloud, WebDAV, and debrid providers.
- Automatic direct/native/debrid route selection with scoring, refresh, and cooldown handling.
- Dropbox, Google Drive, TeraBox, generic HTTP(S), and portable provider support.
- Resumable segmented transfers, persistent byte maps, remote checksum validation, and a fair per-host scheduler.
- Sync preview with per-file Download, Overwrite, Skip, and Rename actions.
- Persistent download history, bandwidth limits, live speed, ETA, and pause/resume controls.
- DNS-over-HTTPS, DNS-over-TLS, proxy rules, diagnostics, and provider health checks.
- Password-encrypted settings/account backups and optional SHA-256 integrity manifests.
- Optional safe archive extraction and PAR2 repair integration.
- System, light, and dark themes, refreshed application branding, and a themed loading animation.
- Automated Windows CI and reproducible release packaging.

### Changed

- Updated the application version and release identity to v1.0.0.
- Updated in-app release links to `Halo1211/cloud-folder-browser-reimagined`.
- Migrated the application and bundled libraries to SDK-style .NET 9 projects.

### Upstream

- Forked from [ptrsuder/cloud-folder-browser](https://github.com/ptrsuder/cloud-folder-browser), retaining the original public-folder browser and local comparison workflow.
