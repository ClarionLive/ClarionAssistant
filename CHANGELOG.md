# Changelog

Release notes for Clarion Assistant live in [`docs/releases/`](docs/releases/) — one file per
release, plus an archive of the older README summaries.

This file is a pointer, deliberately, rather than a second copy. A changelog maintained in two
places drifts, and the version numbers in this project already have to agree across
`Version.props`, the addin manifest and the git tag without a fourth place to keep in step.

| | |
|---|---|
| **Latest release** | [GitHub Releases](https://github.com/ClarionLive/ClarionAssistant/releases/latest) |
| **Per-release notes** | [`docs/releases/`](docs/releases/) — `vX.Y.Z.md` |
| **Older summaries (v5.5 back to v3.0)** | [`docs/releases/CHANGELOG.md`](docs/releases/CHANGELOG.md) |
| **Current + previous release** | summarised in the [README](README.md) under *What's New* |

## Recent releases

- [v5.8.1](docs/releases/v5.8.1.md)
- [v5.8.0](docs/releases/v5.8.0.md)
- [v5.7.0](docs/releases/v5.7.0.md)
- [v5.6.0](docs/releases/v5.6.0.md)
- [v5.5.0](docs/releases/v5.5.0.md)

Earlier versions are listed in [`docs/releases/`](docs/releases/); for 4.x and 3.x no release-notes
file was written, and the summaries in
[`docs/releases/CHANGELOG.md`](docs/releases/CHANGELOG.md) are the only record.

## How releases are versioned

The released version is `Major.Minor.Patch`, set by hand in
[`ClarionAssistant/Version.props`](ClarionAssistant/Version.props). Three things must agree on it
exactly — that file, `<Identity version>` in the shipped `ClarionAssistant.addin`, and the git tag
minus its leading `v`. A build-time check (`CheckAddinVersion`) fails the build if the first two
drift apart, and the installer script no longer keeps its own copy of the number.

The fourth component seen in a DLL's file properties (`5.8.2.1165`) is an auto-incrementing build
counter. It is useful for support and appears in the assembly only.
