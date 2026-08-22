---
name: auto-verify
description: Let a non-technical "monkey user" drive, but prevent spaghetti loops with test-first and auto-reset
license: MIT
compatibility: opencode
---

## What I do
- You say "this is broken" like a monkey — I turn it into a failing test, loop until it passes, and never patch endlessly.

## When to use me
- User says "fix this" without technical detail, or a bug loops.

## How
1. **Test first:** Write the smallest reproduction (e.g. `POST /media/resolve` for the failing Instagram URL). Keep it in `scripts/` or `tests/`. It must fail before the fix.
2. **Loop until green:** Fix, run `build-test.ps1` + the reproduction test. If red, read the error and fix again — do not ask the user.
3. **Circuit breaker:** After 2 failed fixes, `git reset --hard`, split the problem smaller, and start fresh. Never keep patching garbage.
