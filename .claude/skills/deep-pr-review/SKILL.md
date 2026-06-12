---
name: deep-pr-review
description: Deep, evidence-based review of a GitHub pull request in the Voxa repo, with findings posted BACK TO THE PR as a GitHub review — inline comments anchored to the diff plus a summary comment. Gathers the full PR record (description, comments, review threads, CI lanes), cross-references the governing spec in docs/specifications/, audits the complete diff against repo invariants, and verifies claims by building and running the test suite before commenting. Use this whenever the user asks to review a PR (by number or the current branch's PR), check whether a PR is ready to merge, audit a merged PR, or asks "did we miss anything" about a PR — even if they don't say the word "review". The deliverable is the posted GitHub review, not a local report.
---

# Deep PR Review

## The contract

The subject is an **OPEN GitHub pull request** and the deliverable is a **GitHub review
posted on it**: one inline comment per finding, anchored to the diff, plus a summary comment
carrying the verdict and everything that can't be anchored. No local report files, no output
waiting for a human to collect — when this skill finishes, the PR conversation contains the
review.

Post with `event: COMMENT` only. Never `APPROVE` or `REQUEST_CHANGES` — gate decisions
belong to humans; the agent's job is evidence.

**Only open PRs receive comments.** A merged or closed PR's conversation is a historical
record — commenting on it pings participants without a decision to inform. If the PR is not
open, do not post anything to GitHub: say so, and return any requested post-merge audit
findings in the conversation instead.

## Why this process exists

A reviewer who only reads the diff misses the two highest-value bug classes in this repo:

1. **Divergence between claims and code.** PR descriptions, PR comments, and spec acceptance
   criteria all make claims ("zero-cost when unobserved", "never touches the network at
   startup"). The bugs that hurt are the ones where the claim is documented and the code
   quietly doesn't deliver it.
2. **Invariant violations invisible in any single hunk.** A diff hunk can look perfect while
   breaking a cross-cutting rule (a DI resolution that only works on ASP.NET hosts, a test
   that secretly downloads models in the default suite).

So: gather the record first, review the code second, verify before commenting. Real past
finds from exactly this process: `DefaultAgentFactory` resolving `IConfiguration` from DI
(works on ASP.NET, throws on plain `ServiceCollection` hosts), and a WASAPI capture
converter that silently dropped every buffer on `WAVE_FORMAT_EXTENSIBLE` devices. Neither
was visible from the diff alone — both came from asking "under what conditions does this
claim fail?"

**Stay read-only on the repository.** Never check out branches, commit, or mutate the
working tree during a review. Use `gh pr diff` and `git diff <ref>...<ref>` style commands
so concurrent work is safe. The ONLY writes this skill performs are GitHub review comments.

## Step 1 — Resolve the PR

```bash
gh pr view <number-or-current-branch> --json number,title,body,state,baseRefName,headRefName,headRefOid,url
```

- Given a number, use it. Given nothing, resolve the current branch's PR.
- **Check `state` FIRST.** Only `OPEN` PRs are commented on. Merged or closed: say so and
  post nothing — if the user explicitly wants a post-merge audit, run the same process but
  deliver the findings in the conversation, never to the PR.
- **No PR exists?** Stop and say so — this skill reviews pull requests, not loose branches.
  Suggest opening a PR first (`gh pr create`); do not improvise a local review instead.

Record `headRefOid` — inline comments and permalinks anchor against it.

## Step 2 — Gather the record (before reading any code)

Collect every source of claims and obligations:

- **PR body and conversation:** `gh pr view <n> --comments`. Comments frequently document
  scope added after the description was written (and known gaps the author already
  acknowledged — don't later re-report those as discoveries; assess whether they were
  addressed).
- **Existing inline review threads:** `gh api repos/{owner}/{repo}/pulls/<n>/comments`.
  Unresolved threads are open obligations; also, never duplicate a comment that's already
  sitting on the same line making the same point.
- **CI:** `gh pr checks <n>`. Note which lanes ran (ubuntu build & test, Windows Studio,
  local-speech zero-network) and anything failing or skipped.
- **The governing spec.** Feature work here is spec-driven: branch names, commit subjects,
  and PR titles carry a spec id (`VST-001`, `VLS-001`, …). Find the matching HTML document
  in `docs/specifications/` and read it — especially the acceptance criteria and the
  workstream checklist (usually section 9). Specs also carry "implementation refinement"
  notes where the build deliberately diverged from the first draft; treat those as the
  current contract.
- **Ground rules:** read `CLAUDE.md` and `CONTRIBUTING.md` for current invariants and
  conventions. They are authoritative over the summary in Step 4.

## Step 3 — Map the change before judging it

```bash
gh pr diff <n>
git diff --stat <base>...<head>   # shape first
```

Classify files: framework (`src/`), app (`apps/`), tests, docs, CI. Then budget your depth
by blast radius, highest first:

1. Public API and behavioral changes in `src/` (every consumer inherits these)
2. Concurrency, cancellation, resource lifetime, audio hot paths
3. App/view-model logic
4. Tests (reviewed for honesty, not just presence — see Step 4)
5. Docs and CI (reviewed for accuracy against behavior)

For a large diff, the summary comment must say which areas got deep review and which got a
pass-over — silence must never imply approval.

## Step 4 — Review lenses

Work through these in order; each catches what the previous one can't.

**1. Claims vs reality.** Build a list of every concrete claim from the PR body, PR
comments, and spec acceptance criteria. For each: find the code that implements it and the
test that would fail if it were false. A claim with no failing-test counterpart is
"unverified" in the summary, not "done".

**2. Spec conformance.** For each acceptance criterion in the governing spec: met,
partially met, or not met — with file/test evidence. Note scope that shipped beyond the
spec (it needs the same review, and the spec may need an update note).

**3. Correctness deep-dive.** Read the changed code as an adversary, asking "when does
this break?" rather than "does this look right?" Priority areas in this codebase:
- The dual-task processor model: system frames preempt data frames; long-running work must
  honor the per-frame `CancellationToken`; `IUninterruptible` frames must survive. Beware
  cross-channel ordering assumptions — system frames may legitimately overtake data frames.
- Disposal and lifetime: DI scopes vs sessions, `IAsyncDisposable` ordering, what happens
  to in-flight work on stop.
- Audio-thread code: no blocking, no unbounded allocation, bounded channels.
- Format/platform assumptions (the WASAPI-extensible class of bug): enumerate the inputs
  the code can actually receive, not just the ones the happy path produces.

**4. Repo invariants.** Verify the diff against these (confirm current wording in
`CLAUDE.md` / `CONTRIBUTING.md`):
- `Voxa.Core` has zero external dependencies beyond NUlid.
- Config capture: anything needing configuration captures the `IConfiguration` passed to
  `AddVoxa` — never `GetRequiredService<IConfiguration>()` (absent on plain
  `ServiceCollection` hosts like Studio and tests).
- Backpressure: audio/data paths `DropOldest`, control paths `Wait`; deviations documented.
- Processors forward unhandled frames (`StartFrame`/`EndFrame` must reach the sink).
- Diagnostics are zero-cost when disabled (the golden composition test must stay intact).
- Warnings are errors; `internal sealed` by default; records for frames/config; no emojis
  in code.
- Default test suite needs no network and no API keys; model-downloading tests carry
  `[Trait("Category", "LocalModels")]`.
- GPL hygiene: espeak-ng stays out-of-process; the dependency-gate tests must still pass.

**5. Test honesty.** For each new test ask: would it fail if the bug existed? Tests that
mirror the implementation, assert on mocks of the thing under test, or pin incidental
values provide coverage theater. Also: fixed `Task.Delay` waits before asserts are the
repo's documented flake anti-pattern — flag them; condition polling is the convention.

**6. Docs accuracy.** README/docs/CHANGELOG statements added by the PR are claims too —
verify commands run and described behavior matches the code.

## Step 5 — Verify before commenting

Cheap, high-signal checks (run them, don't assume):

```bash
dotnet build Voxa.slnx -warnaserror
dotnet test Voxa.slnx --filter "Category!=LocalModels"
```

Never run the `LocalModels` suite unless the PR touches the model catalogs or cache (it
downloads hundreds of MB).

Then **adversarially re-check every candidate finding**: re-read the actual current file
(not the diff hunk), and try to refute yourself — is there a guard elsewhere, a test that
already covers it, a reason the author did it this way? Post only findings that survive.
A wrong comment on a PR wastes the author's time and erodes trust in every future review;
an honest "I could not verify X" in the summary is always acceptable.

## Step 6 — Post the review

One API call creates the whole review — summary plus all inline comments — so the PR gets
a single review event, not a comment storm:

```bash
# payload.json — write it to a TEMP location, never into the repo:
{
  "event": "COMMENT",
  "body": "<the summary — template below>",
  "comments": [
    {
      "path": "src/Voxa.Core/Processors/FrameProcessor.cs",
      "line": 63,
      "side": "RIGHT",
      "body": "**High — frames can be silently dropped here.**\n\n<what & why it matters>\n\n**Suggested fix:** <concrete change>"
    }
  ]
}

gh api repos/{owner}/{repo}/pulls/<n>/reviews --input payload.json
```

Anchoring rules:
- `line` must be a line that appears in the PR diff; `side: "RIGHT"` for the new version,
  `"LEFT"` only when commenting on deleted code. Use `start_line` + `line` for multi-line.
- A finding whose location is NOT in the diff (e.g. an invariant broken elsewhere by this
  change) goes in the summary body with a permalink:
  `https://github.com/{owner}/{repo}/blob/<headRefOid>/<path>#L<line>`.
- If the API rejects an anchor (422), don't fight it — move that finding into the summary
  body with its permalink and post again.
- Each inline comment starts with its severity in bold: **Critical** (data loss, crash,
  security, broken core guarantee) / **High** (real bug users will hit) / **Medium**
  (edge-case bug or invariant violation) / **Low** (genuine nit — use sparingly; this is a
  deep review, not a style pass). State what, why it matters, and a concrete suggested fix.

Summary body template:

```markdown
## Deep review — <verdict>

One of: **Ready to merge** / **Ready with nits** / **Needs changes** / **Blocked**, plus one
sentence of justification. (An in-conversation post-merge audit uses the same structure with
the verdict **Post-merge audit — N follow-ups**.)

### Spec conformance
Per acceptance criterion: ✅ met / ⚠️ partial / ❌ not met, with evidence. Omit (and say
why) if no spec governs the change.

### Claims audit
Claims from the PR body/comments not covered by inline comments: verified (how) or
unverified.

### Verification
CI lane status; what was run locally (build/test commands) and the results.

### Not reviewed
What was skipped or only skimmed, and why — silence must never imply approval.
```

Findings the author already acknowledged in PR comments belong in the claims audit (was
the acknowledgment honored?), not as new inline comments. After posting, reply in the
conversation with just the review URL and a one-line verdict — the full content lives on
the PR, not in chat.
