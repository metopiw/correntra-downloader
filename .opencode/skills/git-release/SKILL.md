---
name: git-release
description: Create consistent releases and changelogs with gh CLI
license: MIT
compatibility: opencode
metadata:
  audience: maintainers
  workflow: github
---

## What I do
- Draft release notes from `CHANGELOG.md`, propose a version bump, and give you a copy-pasteable `gh` command.

## When to use me
- When you are preparing a tagged release on `main`.

## How
- Read `CHANGELOG.md` and `Directory.Build.props:VersionPrefix`.
- Commit with message `X.Y.Z: summary`, tag `vX.Y.Z`, push, then:
  `gh release create vX.Y.Z --title "Correntra Downloader X.Y.Z" --notes "..."`
- Use `gh auth status` to confirm `metopiw` is logged in; `gh release list` to verify.
