# Changelog

All notable changes to Cloud Folder Browser Reimagined are documented here.

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
