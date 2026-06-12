---
name: deep-pr-review
description: Deep, evidence-based review of a pull request or feature branch in the Voxa repo. Gathers the full PR record from GitHub (description, comments, review threads, CI lanes), cross-references the governing spec in docs/specifications/, audits the complete diff against repo invariants, verifies claims by building and running the test suite, and produces a severity-ranked findings report. Use this whenever the user asks to review a PR, review the current branch, check whether a branch or PR is ready to merge, audit recent changes, do a code review, or asks "did we miss anything" about shipped work — even if they don't say the word "review".
---

# Deep PR Review

## Why this process exists

A reviewer who only reads the diff misses the two highest-value bug classes in this repo:

1. **Divergence between claims and code.** PR descriptions, PR comments, and spec acceptance
   criteria all make claims ("zero-cost when unobserved", "never touches the network at
   startup"). The bugs that hurt are the ones where the claim is documented and the code
   quietly doesn't deliver it.
2. **Invariant violations invisible in any single hunk.** A diff hunk can look perfect while
   breaking a cross-cutting rule (a DI resolution that only works on ASP.NET hosts, a test
   that secretly downloads models in the default suite).

So: gather the record first, review the code second, verify before reporting. Real past
finds from exactly this process: `DefaultAgentFactory` resolving `IConfiguration` from DI
(works on ASP.NET, throws on plain `ServiceCollection` hosts), and a WASAPI capture
converter that silently dropped every buffer on `WAVE_FORMAT_EXTENSIBLE` devices. Neither
was visible from the diff alone — both came from asking "under what conditions does this
claim fail?"

**Stay read-only.** Never check out another branch, commit, or mutate the working tree
during a review. Use `gh pr diff` and `git diff <ref>...<ref>` so concurrent work is safe.

## Step 1 — Establish the subject

```bash
git branch --show-current
gh pr view --json number,title,body,state,baseRefName,url   # PR for the current branch
```

- **No PR yet?** Review the branch against its merge-base instead
  (`git diff $(git merge-base origin/main HEAD)...HEAD`), note in the report that no PR
  exists, and review what a PR would contain — including uncommitted changes if the user
  asked "is this ready to push".
- **PR already merged?** Proceed as a post-merge audit: same rigor, but frame findings as
  follow-up work rather than merge blockers.

## Step 2 — Gather the record (before reading any code)

Collect every source of claims and obligations:

- **PR body and conversation:** `gh pr view <n> --comments`. Comments frequently document
  scope added after the description was written (and known gaps the author already
  acknowledged — don't later re-report those as discoveries; assess whether they were
  addressed).
- **Inline review threads:** `gh api repos/{owner}/{repo}/pulls/<n>/comments`. Unresolved
  threads are open obligations.
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
gh pr diff <n>            # or the merge-base diff from Step 1
git diff --stat <range>   # shape first
```

Classify files: framework (`src/`), app (`apps/`), tests, docs, CI. Then budget your depth
by blast radius, highest first:

1. Public API and behavioral changes in `src/` (every consumer inherits these)
2. Concurrency, cancellation, resource lifetime, audio hot paths
3. App/view-model logic
4. Tests (reviewed for honesty, not just presence — see Step 4)
5. Docs and CI (reviewed for accuracy against behavior)

For a large diff, say in the report which areas got deep review and which got a pass-over —
silence must never imply approval.

## Step 4 — Review lenses

Work through these in order; each catches what the previous one can't.

**1. Claims vs reality.** Build a list of every concrete claim from the PR body, PR
comments, and spec acceptance criteria. For each: find the code that implements it and the
test that would fail if it were false. A claim with no failing-test counterpart is
"unverified" in the report, not "done".

**2. Spec conformance.** For each acceptance criterion in the governing spec: met,
partially met, or not met — with file/test evidence. Note scope that shipped beyond the
spec (it needs the same review, and the spec may need an update note).

**3. Correctness deep-dive.** Read the changed code as an adversary, asking "when does
this break?" rather than "does this look right?" Priority areas in this codebase:
- The dual-task processor model: system frames preempt data frames; long-running work must
  honor the per-frame `CancellationToken`; `IUninterruptible` frames must survive.
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
values provide coverage theater. Flag claimed behavior whose test wouldn't catch its
absence.

**6. Docs accuracy.** README/docs/CHANGELOG statements added by the PR are claims too —
verify commands run and described behavior matches the code.

## Step 5 — Verify before reporting

Cheap, high-signal checks (run them, don't assume):

```bash
dotnet build Voxa.slnx -warnaserror
dotnet test Voxa.slnx --filter "Category!=LocalModels"
```

Never run the `LocalModels` suite unless the PR touches the model catalogs or cache (it
downloads hundreds of MB).

Then **adversarially re-check every candidate finding**: re-read the actual current file
(not the diff hunk), and try to refute yourself — is there a guard elsewhere, a test that
already covers it, a reason the author did it this way? Report only findings that survive,
each with `file:line` evidence. A fabricated or sloppy finding costs more than it's worth;
an honest "I could not verify X" is always acceptable.

## Step 6 — The report

Use exactly this structure:

```markdown
# PR Review: <title> (#<n>)        # or "Branch review: <branch>" when no PR exists

## Verdict
One of: **Ready to merge** / **Ready with nits** / **Needs changes** / **Blocked** — plus
one sentence of justification.

## Findings
| # | Severity | Where | What & why it matters | Suggested fix |
Severity scale — Critical: data loss, crash, security, or a broken core guarantee.
High: a real bug users will hit. Medium: edge-case bug or invariant violation.
Low: genuine nit (use sparingly; this is a deep review, not a style pass).

## Spec conformance
Per acceptance criterion: ✅ met / ⚠️ partial / ❌ not met, with evidence. Omit the section
(and say why) if no spec governs the change.

## Claims audit
Claims from the PR body/comments not already covered above: verified (how) or unverified.

## CI & local verification
Lane-by-lane CI status; what you ran locally and the results.

## Not reviewed
What you skipped or only skimmed, and why.
```

Findings the author already acknowledged in PR comments belong in the claims audit (was
the acknowledgment honored?), not in Findings as new discoveries.
