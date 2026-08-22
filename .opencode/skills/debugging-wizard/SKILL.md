---
name: debugging-wizard
description: Turn vague "it's slow / not working" into a falsifiable hypothesis and the cheapest proof
license: MIT
compatibility: opencode
---

## What I do
- Translate a symptom into 2–3 testable hypotheses, run the cheapest proof for each, and keep only what the evidence supports.

## When to use me
- When a bug loops, when "it used to work" or when speed/behavior is intermittent.

## How

1. Write hypotheses with a number: "H1: 32 segments cause 429. Test: 1 vs 4 vs 8 vs 32 parallel Range fetches to same host and count 429s."
2. Run the test — prefer a one-file `python -c` or a small `scripts/*.ps1` over guessing. Read files before changing them.
3. Keep the winner, drop the rest. If evidence contradicts a prior claim, say so plainly.
4. Fix only the proven cause; keep the change as small as the test allows. Verify with the same test again.
