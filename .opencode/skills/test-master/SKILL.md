---
name: test-master
description: Write and run the smallest test that would have caught the bug
license: MIT
compatibility: opencode
---

## What I do
- Add the missing E2E or unit test before declaring a fix done.

## When to use me
- After any fix to transfer, bridge, or media code.

## How
- Prefer existing harnesses: `dotnet test`, `scripts/test-bridge.ps1`, `scripts/test-media-e2e.ps1`.
- For new behavior, add a test that fails without your change and passes with it. Keep it in `tests/` or `scripts/`.
- Run `build-test.ps1` (build + all tests) and the relevant `test-*.ps1` before marking Done.
