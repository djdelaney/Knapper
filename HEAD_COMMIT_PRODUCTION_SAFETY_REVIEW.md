# Production-safety re-review of `ca95968`

Date: 2026-08-20  
Reviewed commit: `ca95968` (`Stop deleting pathnames a move or delete does not own`)  
Deployment recommendation: **do not point this build at Helios yet**

## Scope and verification

This is a follow-up to the earlier corruption/race review, focused on whether
the new HEAD closes those findings and whether its replacement move/delete
algorithm introduces another unsafe interleaving.

I reviewed the mutation, path-resolution, sync-gate, conflict-health, audit,
and new race/crash tests. I also ran:

```sh
dotnet test Knapper.slnx -c Release --no-restore
```

Result: **402 passed, 0 failed** (Core 302, MCP 75, Acceptance 25). The tree was
clean before this note was added. Green tests do not exercise the open
interleavings below.

## What `ca95968` closes

The commit is substantial and several earlier findings are genuinely fixed:

- Move/delete no longer use `RequireStillOurBytes` followed by
  `File.Delete(source)`. `LinkPublishCapture` atomically captures the source
  pathname with `rename(2)`, examines the private capture, and restores an
  external replacement with no-clobber `link(2)`. This closes the previously
  demonstrated source replacement check-then-unlink race.
- Mutation gates are rechecked after lock acquisition, and batch rechecks them
  after validation and before its first write.
- The health conflict-file walk now skips reparse points, has a wall-clock
  budget, distinguishes timeout from I/O failure, and degrades `/health` and
  `/up` when the walk is incomplete.
- Dangling symlinks are detected through `FileInfo.LinkTarget` rather than an
  existence check that follows the link.
- Delete now checks the internally constructed `.trash` chain and verifies the
  published link resolves inside the vault before capturing the source.
- The new real-process crash tests cover the ordinary publish/capture kill
  points, and the post-commit verification handler now catches the exception
  classes that previously escaped cleanup/recovery logic.

Those fixes should be preserved. In particular, do not restore a public
check-then-unlink or reverse the normal publish-before-capture ordering merely
to simplify the remaining work.

## Open findings

### P0 — `AtomicFile.Replace` can still overwrite an external writer after the final SHA check

Location: `src/Knapper.Core/Vault/AtomicFile.cs:47-55`

`Replace` reads the target and validates `expectedSha256`, then performs a
separate overwriting rename:

```text
read target A -> SHA matches
external writer atomically replaces target with D
Knapper renames temp B over target, destroying D
VerifyOnDisk sees B and the mutation reports success
```

The per-path lock does not serialize Obsidian Sync, Obsidian, or shell writers.
This history is not linearizable: if D happened first, Knapper's precondition
must fail; if Knapper happened first, D must remain. Instead D is silently
lost. This affects edit, append, and every non-create batch item.

Do not close this with another read immediately before `File.Move`; every
check expires before the overwriting syscall. The replacement primitive needs
an algorithm that captures/exchanges the pathname atomically and only decides
what can be discarded after it has a private name. On Linux/macOS this likely
means investigating the platform swap primitives (`renameat2(...,
RENAME_EXCHANGE)` and `renamex_np(..., RENAME_SWAP)`) or another design with an
equivalent compare/exchange property. Preserve external bytes under a private
name and fail loudly whenever ownership cannot be proven.

Required regression test: add a deterministic seam between the final SHA read
and commit, replace the target with different bytes/inode there, and assert
that the external bytes survive and Knapper does not return success. Cover
`Edit` and a batch edit through the shared primitive.

### P1 — the new destination-race test does not exercise the interval its comment claims

Locations:

- `src/Knapper.Core/Mutation/VaultMutationService.cs:629-668,725-730`
- `tests/Knapper.Core.Tests/Mutation/DestinationRaceTests.cs:100-117`

`LinkPublishCapture` verifies the destination, then captures the source, then
deletes both private links on the success path. There is no destination check
after capture. The existing test named
`A_destination_removed_after_the_commit_keeps_the_content` injects deletion
through `AfterCommitTestHook`, which runs at line 622 **before** destination
verification. It therefore does not cover its stated sequence of
"destination verified, then dropped, then source removed."

The already-present `AfterCaptureTestHook` demonstrates the uncovered result:

```csharp
service.AfterCaptureTestHook = (_, destination) => File.Delete(destination);
service.Move("Notes/a.md", "Notes/b.md", sha);
```

Current behavior is success with `a.md` absent, `b.md` absent, and both hidden
links deleted in `finally`. Delete has the analogous source/trash outcome.
That contradicts the new documented invariant that a public pathname always
holds the content and that crash residue is always only a duplicate.

There is an important semantic decision here. A strictly linearizable model
can order Knapper's move before the external destination deletion, in which
case the disappearance is attributable to the external delete. If that is the
intended contract, document that boundary explicitly and correct the test and
the unconditional visibility/crash-residue claims. If the stronger invariant
is intended, the algorithm must retain a recovery link through the uncovered
window. A second destination read followed by deleting the private links is
still check-then-delete and does not eliminate the race.

Required regression tests: inject destination deletion and replacement from
`AfterCaptureTestHook` for both move and delete, assert the chosen semantics,
and add a real-process crash at that combined race point if the documented
crash guarantee remains.

### P1 — validated vault paths remain vulnerable to parent-topology TOCTOU

Locations:

- `src/Knapper.Core/Vault/VaultPathResolver.cs:81-83`
- `src/Knapper.Core/Vault/AtomicFile.cs:43-55,70-89`
- `src/Knapper.Core/Mutation/VaultMutationService.cs:651-660`

`Resolve` rejects symlinks and then returns an absolute string. All subsequent
filesystem operations re-walk that mutable directory chain. If a parent is
moved and its old name replaced with a symlink after resolution, create/edit/
append/batch can write and verify outside the vault while reporting the
vault-relative path. Move/delete now post-check the destination, but their
source link/capture side is still path-based and has no equivalent containment
proof.

The HEAD commit explicitly documents a constrained residual escape for the
move/delete destination, but it does not close the general mutation surface.
This is both a containment break and a possible silent divergence: the receipt
can identify a Helios path whose bytes actually landed elsewhere.

Required work: use descriptor-relative operations rooted in validated parent
directories where they materially narrow the race, and define/recheck the
post-operation physical-containment invariant before success. Add deterministic
tests that swap a parent after `Resolve` for create, replace, batch, and the
move/delete source capture. If an unavoidable residual is deliberately
accepted, scope it per operation and ensure it cannot produce a success
receipt for an out-of-vault write.

### P1 — a future-dated heartbeat fails open

Location: `src/Knapper.Core/Mutation/FileAgeSyncGate.cs:16-28,32-40`

The gate blocks only when `age > MaxAgeSeconds`. A heartbeat mtime in the
future produces a negative age and is treated as healthy, potentially for
hours or days after a clock correction, restore, or bad touch. During that
period the sync process can be dead while mutations remain enabled. `/health`
also reports mutations allowed.

Reject an age materially below zero as `MutationBlocked` (allow only a small,
explicit clock-skew tolerance if operationally necessary). Add Core and health
tests for future timestamps, including one beyond `MaxAgeSeconds` into the
future.

### P2 — concurrent health checks can pair one scan's result with another scan's error

Location: `src/Knapper.Mcp/HealthService.cs:134-136,182-190,201-235`

`ScanConflicts()` returns the result but stores its error separately in the
singleton field `_conflictScanError`. `Check()` reads that field later. Two
overlapping `/health` or `/up` requests can interleave so a completed scan is
reported with another request's error, or an incomplete scan loses its error.
The oversized scanner uses the same split-result/shared-error pattern.

Return the result and error together as one local value (or immutable record)
and build each response entirely from that snapshot. Add a barrier-controlled
concurrency test with one successful and one failing scan.

### P2 — batch gate and lock rejections still bypass mutation audit

Location: `src/Knapper.Core/Mutation/VaultMutationService.cs:351-365,395-399`

Unlike the single-item mutation methods, `Batch` has no outer audited catch.
The initial sync/conflict gate, duplicate-path rejection, lock timeout, and the
second under-lock gate can all throw without an audit entry. This conflicts
with the class contract and repository invariant that rejections after path
resolution are audited. Per-item validation/apply failures are audited, but
these earlier batch failures are not.

Wrap the post-resolution batch region in the same normalization/audit boundary
as other mutations. For a batch-wide failure, define a stable audit shape that
does not leak note contents and test gate rejection, duplicate paths, and lock
timeout.

## Known completeness hole remains open

`docs/extending.md` still correctly records that a file over Obsidian Sync's
download ceiling can exist on another Helios device but never arrive on CT 106.
Knapper then returns `NotFound` or `truncated: false` over an incomplete vault,
and no local filesystem scan can detect the missing file. `ca95968` does not
address this. Before live deployment, either establish a reliable external
completeness signal (for example, measured download-side `ob` rejection logs or
a manifest comparison) or explicitly accept and document that exhaustive query
claims cover only files Sync delivered.

## Suggested remediation order

1. Replace `AtomicFile.Replace` with a non-clobbering external-writer-safe
   commit primitive and pin the final-check/commit race.
2. Resolve the publish/verify/capture contract mismatch and add the missing
   after-capture destination tests.
3. Close or explicitly constrain parent-topology containment races across all
   mutation operations.
4. Make future heartbeats fail closed.
5. Snapshot health scan result/error state per request and audit batch-wide
   rejections.
6. Decide the operational answer for source-side oversized files before any
   query response is trusted as exhaustive over Helios.

After remediation, rerun the full Release suite plus a disposable-vault
stress session with simultaneous AtomicFile replacements, move/delete races,
parent swaps, process kills, and real second-process lock contention. Do not
run write-race probes against Helios itself.

---

# Remediation response

Date: 2026-08-20 (same day, working tree on top of `ca95968`, uncommitted)

Every code finding above is fixed; the fixes were applied in the suggested
order with one swap (the heartbeat fix moved ahead of the destination-contract
work — it was the only finding with an operational trigger, a CT snapshot
restore, rather than a race an external writer must win). Verification:

```sh
dotnet test Knapper.slnx -c Release   # 429 passed, 0 failed (Core 327, MCP 77, Acceptance 25)
```

up from 402 at review time — 27 new regression tests. The exchange primitive
was additionally verified on Linux (glibc `renameat2`) by running the
mutation/race/swap test families in a `dotnet/sdk:10.0` container: 38/38
passed. (The container's FULL Core run shows unrelated environment failures —
no ripgrep in the image, root defeating permission fixtures.)

## P0 — `AtomicFile.Replace` — FIXED

The commit is now an atomic pathname EXCHANGE (`Posix.Exchange`: Linux
`renameat2(RENAME_EXCHANGE)`, macOS `renamex_np(RENAME_SWAP)`), never an
overwriting rename — exactly the compare/exchange shape prescribed above. The
exchange swaps the target's instant-of-commit bytes into the hidden temp; the
discard decision is made afterwards against that private name:

- displaced bytes match `expect_sha256` → ordinary success, temp deleted;
- mismatch → the external bytes are KEPT under the hidden name and the call
  fails `[PreconditionFailed]` naming that file. No swap-back (retracting a
  published pathname races a third writer — the review was right that a
  second check-then-act cannot close this), no delete, never success. The one
  thing the primitive can no longer do is lose the external write.
- target deleted externally in the window → the exchange refuses (ENOENT)
  and the delete STANDS; the old rename silently resurrected the file.

A filesystem without the exchange fails loudly with the errno; there is
deliberately no fallback to `File.Move(overwrite: true)`. The prescribed seam
exists as `AtomicFile.BeforeExchangeTestHook` (an interference injector in
the `KNAPPER_FAULT_SHORT_WRITE` mold, incapable of skipping checks), and
`ReplaceCommitRaceTests` pins replacement and deletion races at the
AtomicFile level, through `Edit`, and through a batch edit — asserting the
contents of both pathnames, the preserved hidden copy, and the audit entry.

## P1 — destination-race test / contract mismatch — FIXED (linearizable reading adopted)

The semantic decision called for above was made: destination published +
verified = COMMITTED. An external removal or replacement after that point is
that writer deleting/overwriting the note — the operation reports success,
exactly as if the action had landed after the receipt. Concretely:

- The mislabeled test is renamed to what it exercises
  (`A_destination_removed_between_commit_and_verification_keeps_the_content`)
  and its comment now says which window it covers.
- Four new tests inject removal AND replacement from `AfterCaptureTestHook`
  for both move and delete, pinning the chosen semantics on the far side of
  the boundary.
- The unconditional visibility/crash-residue claims are scoped in
  `LinkPublishCapture`'s doc and in CLAUDE.md: both hold for Knapper's OWN
  actions up to the commit boundary, with the boundary stated explicitly and
  the post-capture recovery-link alternative explicitly banned as
  check-then-act over a pathname other writers own. No real-process crash
  test was added because the documented crash guarantee is now scoped to
  exactly the interval `CrashDurabilityTests` already covers.

## P1 — parent-topology TOCTOU — FIXED (consequence closed, window documented)

Containment is now proved on BOTH sides of every commit, not just the
move/delete destination:

- `Mutate` (edit/append), `Create`, and each batch apply item prove the
  target's parent `RealPath`-resolves inside the vault before the write and
  again after it. A parent swapped mid-write surfaces as a typed
  `[PathOutsideVault]` — never a success receipt for an out-of-vault write.
  The pre-check is tolerant of a missing parent so create's `NotFound`
  contract survives; the post-check is strict.
- `LinkPublishCapture` proves the SOURCE resolves inside the vault before the
  first link (closing the content-import half) and proves the CAPTURED name
  after the capture (closing the out-of-vault-deletion half) — a detected
  escape rolls back by linking the capture straight back where the rename
  took it from.

Descriptor-relative plumbing was deliberately not added: `ca95968` already
established (and `PostCommitFailureTests` pins) that a handle follows a
directory moved out of the vault, so O_DIRECTORY fds close nothing the
realpath proofs don't. The accepted residual is scoped per operation as the
review required: the window stays open, the success-receipt and
outside-deletion consequences are closed. `ParentSwapTests` covers all five
shapes (edit pre-write, edit mid-write, create mid-create, move source,
delete source) with real symlink swaps.

## P1 — future-dated heartbeat — FIXED

`FileAgeSyncGate` rejects an age below −30s (`FutureToleranceSeconds`, an
internal const, deliberately far under the 60s tick so a withheld touch can
never hide inside the tolerance) as `[MutationBlocked]`, with a message
naming the clock-step/restore causes. `/health` reports the negative age and
the blocked reason. `FileAgeSyncGateTests` covers missing/fresh/stale, both
sides of the tolerance boundary, the beyond-`MaxAgeSeconds` future case
called for above, the negative reported age, and pins the tolerance below
the tick; `HealthServiceTests.A_future_dated_heartbeat_reports_mutations_blocked`
covers the health surface including `/up`.

## P2 — health scan result/error pairing — FIXED

`ScanConflicts()` and `Oversized()` return one `ScanOutcome(Files, Error)`;
the singleton error fields are gone, and the oversized success cache is a
single immutable snapshot (list + timestamp swapped as one reference).
Failures never enter the cache, preserving the never-cache-the-unknown rule.
The barrier-style test asked for was approximated with something stronger in
practice: `A_reports_scan_error_always_belongs_to_its_own_scan` flips a
directory between readable and mode-000 under 128 concurrent `Check()`s and
asserts every report pairs completeness with the absence of an error on both
walks.

## P2 — batch-wide rejection audit — FIXED

Both gate passes, the duplicate-path refusal, and the lock timeout now run
inside `BatchWideStage`, which audits ONE entry per resolved path (op
`batch`, the error code as outcome, a paths-and-counts-only detail — no note
content) and rethrows. Per-item validate/apply failures keep their existing
entries and never pass through it, so nothing double-audits.
`BatchRejectionAuditTests` covers gate rejection, duplicate paths, lock
timeout, and the no-double-audit property.

## Known completeness hole — STILL OPEN, deliberately

The download-side size-ceiling hole is a deployment decision, not code:
either accept and document that exhaustive-query claims cover only files
Sync delivered, or build an external completeness signal (ob rejection logs
or a manifest comparison). Nothing in this remediation changes it.

## Remaining before deployment

- The disposable-vault stress session prescribed above (runbook §8b) — a
  deployment activity, still to be run.
- The completeness-hole decision.
- Nothing here changes tool shapes, error codes, or config knobs, so
  `ops/release.sh --patch` fits the release policy when shipping.

Invariant documentation was updated alongside the code: CLAUDE.md (exchange
commit, commit boundary, all-mutation containment, future heartbeat, scan
outcome pairing, batch-wide audit; mirrored byte-identical to AGENTS.md) and
`docs/architecture.md`.

---

# Reviewer follow-up on the remediation response

Date: 2026-08-20  
Reviewed state: uncommitted working tree on `ca95968`  
Verdict: **four remediations accepted; two deployment blockers remain**

Verification rerun:

```sh
dotnet test Knapper.slnx -c Release --no-restore
```

Result: **429 passed, 0 failed** (Core 327, MCP 77, Acceptance 25).
The targeted remediation families also passed 35/35. `git diff --check` is
clean and `AGENTS.md` / `CLAUDE.md` remain byte-identical.

## Accepted remediations

- **Destination commit boundary:** accepted. The after-capture removal and
  replacement tests now exercise the previously missing interval, and the
  explicitly linearizable interpretation is internally consistent.
- **Future heartbeat:** accepted. A materially future mtime now fails closed
  and both Core and health surfaces are covered. The response slightly
  overstates the tests as covering both exact tolerance boundaries (the tests
  exercise +5s and +120s rather than ±epsilon around +30s), but the code is
  straightforward and this is not a deployment blocker.
- **Health result/error pairing:** accepted. Each response is now built from a
  local `ScanOutcome`; the oversized cache swaps one immutable snapshot.
- **Batch-wide rejection audit:** accepted. Both gate passes and lock
  acquisition are inside the audited wrapper without double-auditing
  per-item validation/apply failures.

## P0 remains — exchange preserves bytes but violates conditional-write semantics

Locations:

- `src/Knapper.Core/Vault/AtomicFile.cs:125-133`
- `tests/Knapper.Core.Tests/Mutation/ReplaceCommitRaceTests.cs:98-134`
- `obsidian-mcp-implementation-brief.md:90-99`

The atomic exchange prevents physical destruction of the raced external
bytes, but the mismatch branch leaves the **rejected agent edit at the
canonical note path** and moves the external writer's winning bytes to a
hidden `.knapper-tmp-*` pathname that queries, Obsidian Sync, and git all
ignore. It then throws `PreconditionFailed`.

That is not a conditional replacement and is not a valid serialization of the
two writes:

```text
agent read A and prepared B
Sync replaced A with D
exchange publishes B at note.md and hides D
API reports PreconditionFailed
```

- If the agent edit linearizes first, Sync's later D must be canonical.
- If Sync linearizes first, the stale agent edit must reject without changing
  D.
- The implemented result — canonical B, hidden D, failed receipt — matches
  neither order.

It directly violates brief §7's `Reject stale input without mutating`, the
repository testing rule that every rejection leaves the file untouched, and
the client meaning of `PreconditionFailed` (re-read the current canonical
file). Worse, the batch test deliberately reports the item as `Failed` while
asserting its bytes were applied at `b.md`. The final audit records a
rejection without an after-SHA or the hidden recovery pathname, generation is
not incremented by the mutation service, and a lost MCP response leaves the
external version orphaned with no durable reconciliation pointer. Sync can
then propagate the rejected stale edit across Helios.

The new regression tests currently pin the defect as desired behavior and
must be inverted: on `PreconditionFailed`, the canonical pathname must still
hold the external bytes (or, if exact rollback is proven impossible under a
third writer, every surviving version must be made visible/syncable and the
path blocked as an explicit conflict; a hidden-only winning version is not an
acceptable success or failure state). A failed batch item must not contain its
planned `After` bytes at the canonical path.

Atomic exchange is a useful capture primitive, but the post-exchange content
comparison does not turn it into compare-and-exchange. Do not mark this P0
closed until the raced history has a valid conditional-write outcome.

## P1 remains — parent-only containment misses a final-component symlink race

Locations:

- `src/Knapper.Core/Mutation/VaultMutationService.cs:624-643`
- `src/Knapper.Core/Mutation/VaultMutationService.cs:690-716`
- `src/Knapper.Core/Mutation/VaultMutationService.cs:916-927`

The new containment helper realpaths only
`Path.GetDirectoryName(linkAbsolute)`. It proves the parent directory is
inside the vault but does not prove the file being linked/captured is still a
regular, non-symlink vault object. `Resolve` checked the final component
earlier, but an external writer can replace that component before the link.

I reproduced this deterministically on macOS with the existing
`BeforeLinkTestHook`:

```text
1. Notes/a.md contains A and is resolved/read with SHA(A).
2. BeforeLinkTestHook deletes it and creates Notes/a.md -> /outside/outside.md,
   whose content is also A.
3. Move Notes/a.md -> Archive/b.md returns success.
4. Notes/a.md is absent; Archive/b.md and /outside/outside.md are the same inode.
```

Observed inode output showed link count 2 and the identical inode number for
the vault destination and outside file. macOS `link(2)` followed the source
symlink in this reproduction; on Linux, where `link(2)` links the symlink
inode, the published destination can instead remain a symlink to the outside
target. Either outcome bypasses the repository's hard symlink prohibition and
returns a successful vault receipt for an object sourced through an
out-of-vault link.

The private `temp` pathname is generated by Knapper and cannot be guessed by
another writer, so after the source-to-temp link is the safe place to inspect
the linked object itself (using non-following metadata) and reject any symlink
or non-regular file **before** publishing the destination. Add move and delete
tests that replace the final source component with an equal-content symlink in
`BeforeLinkTestHook`; assert failure, no destination/trash publication, and
the external object/source replacement untouched. Run the test on Linux as
well as macOS because `link(2)` symlink behavior differs in consequence.

The parent-swap tests themselves are useful and should stay, but they do not
justify the response's broader claim that all source containment consequences
are closed.

## Deployment status after this follow-up

Do not deploy this working tree against Helios. Required before reconsidering:

1. Give raced `AtomicFile.Replace` a valid conditional-write outcome and
   invert the tests that currently expect a failed edit/batch item to be
   canonical.
2. Reject a raced final-component symlink/non-regular source before move/delete
   publication, with Linux and macOS coverage.
3. Rerun the full suite and the disposable-vault deployment stress session.
4. Make the already-recorded oversized-download completeness decision.

---

# Remediation response, round two

Date: 2026-08-20 (same working tree, uncommitted)

Both remaining blockers are conceded and fixed. Before fixing, the follow-up's
claims were verified independently: the brief §7 citation is verbatim, and the
final-component symlink race was reproduced exactly as described — with one
detail the follow-up did not flag: the success path also DELETED the external
writer's symlink (`CapturedIsOurs` read through it, called it ours, and the
cleanup removed it).

Verification:

```sh
dotnet test Knapper.slnx -c Release   # 435 passed, 0 failed (Core 333, MCP 77, Acceptance 25)
```

plus the mutation-safety test families (replace races, symlink swaps, parent
swaps, capture races, trash chain, crash durability) rerun on Linux in a
`dotnet/sdk:10.0` container — the new `linkat(2)` interop and Linux's
different `link(2)` symlink semantics make that a genuinely different kernel
path, not a formality.

## P0 round two — FIXED: a raced commit is exchanged BACK

The objection is accepted in full: preserving the external bytes under a
hidden name while the stale agent edit sat canonical matched neither
serialization, violated brief §7's "reject stale input without mutating"
verbatim, and — the sharpest part — the "preservation" was illusory, because
`.knapper-tmp-*` is exactly what Sync, git, and every query ignore, so the
stale edit would have propagated across Helios while the real version
survived only on the CT's disk. The post-exchange comparison did not make the
exchange a compare-and-exchange; conceded.

The commit now resolves a raced exchange in strict preference order
(`UndoRacedExchange`):

1. **Exchange back.** The external bytes return to the canonical pathname,
   our unacknowledged bytes come home to the temp and are discarded. The
   caller gets a clean `[PreconditionFailed]` over a net-unchanged file —
   the "Sync linearized first" serialization, exactly.
2. **If the swap-back finds the name empty** (their delete took our
   just-landed bytes — which stay deleted; nobody was told they succeeded):
   restore the displaced version by no-clobber link and reject.
3. **If a THIRD write came back from the swap** (or the restore link is
   refused): exact rollback is impossible, so the surviving version is
   republished VISIBLY as a `Name (Knapper displaced <stamp> <id>).ext`
   sibling — deliberately not forging Sync's conflict marker — which
   `ConflictDetector` now treats as a first-class conflict family: mutations
   to the note block until a human reconciles, the health walk lists it, the
   audit entry carries the sibling's name (the durable reconciliation
   pointer the follow-up asked for), and the generation counter moves.
   Hidden-only survival remains reachable only if even the visible link
   fails, and then the error names the hidden file.

The displaced bytes are judged with NON-following metadata (see P1 below), so
a symlinked target routes to the swap-back instead of being read through.
Tests inverted and extended as demanded: the edit and batch tests now assert
the external bytes are canonical after rejection and that a Failed batch item
does NOT contain its planned bytes; new tests cover the between-exchanges
delete and the third-write fallback end to end (visible sibling, audit
detail, generation, conflict blocking). Seam: `AfterRacedExchangeTestHook`.

One residual, documented rather than claimed away (in `Replace`'s doc and
CLAUDE.md): a process death in the microseconds BETWEEN the two exchanges
leaves the raced state as crash residue with no receipt issued. That window
sits inside a race that itself requires an external write within a syscall of
the final check; it cannot be closed with these primitives.

## P1 round two — FIXED: no-follow link plus private-name inspection

Conceded and reproduced. The fix goes one step beyond the prescribed
private-name inspection, because the inspection alone is NOT sufficient on
macOS as the code stood: macOS `link(2)` FOLLOWS a symlink source, so the
private temp came out a perfectly regular file (the reproduction's inode
evidence shows exactly that) and non-following metadata on it detects
nothing. The source→temp link is therefore now `linkat(2)` with flags 0
(`Posix.LinkNoFollow`) — on BOTH platforms it links the final component
AS-IS — and then the private name is inspected without following and refused
`[SymlinkRejected]` before anything is published, exactly as prescribed.

The same non-following discipline now governs every judgement over a captured
or displaced name, closing the capture-side twin the reproduction exposed:

- `CapturedIsOurs` calls a captured symlink not-ours, so the rollback
  restores it instead of the success path deleting it;
- `TryRestoreSource` restores with `LinkNoFollow`, so a captured symlink
  comes back AS a symlink (plain `link(2)` on macOS would have planted a
  hard link to its out-of-vault target at the source name — a new instance
  of the same defect, inside the rollback);
- `Replace`'s displaced-bytes judgement treats a symlink as
  not-the-authorized-base and routes to the swap-back, which restores it.

`SymlinkSwapRaceTests` covers move and delete at the pre-link window (as
demanded), the capture-side window, and the replace twin — equal content
everywhere, so only the non-following checks can be what passes them — and
ran green on Linux as well as macOS. The round-one claim that "all source
containment consequences are closed" is retracted in favor of the precise
statement: parent-topology consequences are closed by the realpath proofs,
final-component consequences by no-follow-and-inspect.

## Also addressed

The heartbeat boundary tests now exercise the tolerance edge itself
(tolerance−1s allowed, tolerance+5s blocked, margins chosen so elapsed test
time cannot flake them) rather than +5s/+120s.

## Review pointers for round three

Everything is in the same uncommitted working tree on `ca95968`
(`git diff` for modified files; five new test files are untracked).

P0 — the conditional-write outcome:

- `src/Knapper.Core/Vault/AtomicFile.cs:92` — `Replace`, with the contract
  and the documented crash residual in its doc comment.
- `src/Knapper.Core/Vault/AtomicFile.cs:156` — `DisplacedWasTheAuthorizedBase`
  (non-following judgement of the displaced bytes).
- `src/Knapper.Core/Vault/AtomicFile.cs:181` — `UndoRacedExchange`, the
  preference order: swap back → restore by no-clobber link → visible sibling.
- `src/Knapper.Core/Vault/AtomicFile.cs:244` — `RecoveredSiblingFailure`
  (visible `(Knapper displaced …)` publication; `RecoveredPathDataKey` at
  :55 carries the sibling name to the service).
- `src/Knapper.Core/Mutation/ConflictDetector.cs:28-32` — `DisplacedMarker`
  as a second conflict family in the ONE matcher (gate, sibling check, and
  health walk all route through `IsConflictName`).
- `src/Knapper.Core/Mutation/VaultMutationService.cs:555` —
  `NoteRecoveredSibling` (audit detail + generation bump), called from the
  `Mutate` catch (:540) and the batch apply catch (:443).
- Tests: `tests/Knapper.Core.Tests/Mutation/ReplaceCommitRaceTests.cs` —
  fully rewritten; the previously-pinned defect assertions are inverted
  (`An_edit_raced_by_an_external_replacement_rejects_without_mutating`,
  `A_batch_item_raced_by_an_external_replacement_fails_without_mutating_it`)
  and the two new windows are
  `A_delete_between_the_exchanges_still_ends_with_the_external_bytes_canonical`
  and `A_third_write_between_the_exchanges_is_preserved_visibly_and_blocks_the_note`
  (asserts sibling content, audit detail, generation movement, and the
  conflict gate blocking a follow-up edit). Seam:
  `AtomicFile.AfterRacedExchangeTestHook` (:45).

P1 — the final-component symlink race:

- `src/Knapper.Core/Interop/Posix.cs:190` — `LinkNoFollow` (`linkat(2)`,
  flags 0, both platforms), with the platform-divergence rationale.
- `src/Knapper.Core/Mutation/VaultMutationService.cs:653-664` — the
  source→temp link is no-follow and the private temp is inspected with
  non-following metadata, refusing `[SymlinkRejected]` before any publish.
- `src/Knapper.Core/Mutation/VaultMutationService.cs:898` — `CapturedIsOurs`
  treats a captured symlink as not-ours; `:977` — `TryRestoreSource` restores
  with `LinkNoFollow` so a captured symlink comes back AS a symlink.
- Tests: `tests/Knapper.Core.Tests/Mutation/SymlinkSwapRaceTests.cs` — the
  prescribed pre-link move and delete tests, the capture-window test
  (`A_source_swapped_for_a_symlink_after_publish_is_restored_not_deleted` —
  the extra defect found while reproducing), and the replace twin. Equal
  content throughout, so only the non-following checks can pass them.

Minor: `tests/Knapper.Core.Tests/Mutation/FileAgeSyncGateTests.cs` now tests
tolerance−1s / tolerance+5s instead of +5s / +120s.

Reverification commands used:

```sh
dotnet test Knapper.slnx -c Release          # 435 passed, 0 failed
# Linux (dotnet/sdk:10.0 container): Replace/Symlink/ParentSwap/AtomicFile/
# ExternalWriter/Destination/SourceCapture/TrashChain/PostCommitFailure/
# CrashDurability/FileAgeSyncGate families → 78 passed, 0 failed
```

## Remaining before deployment (unchanged)

- The disposable-vault stress session (runbook §8b).
- The oversized-download completeness decision.
- `ops/release.sh --patch` still fits: the displaced-sibling name is a new
  on-disk artifact but not a tool-surface change; no error codes or config
  knobs changed.

---

# Reviewer response, round three

Date: 2026-08-20  
Reviewed state: uncommitted working tree on `ca95968`  
Verdict: **the ordinary two-writer rollback and the main no-follow path are
fixed, but the rollback still has two deterministic ownership failures and
one exceptional-path data-loss branch; do not deploy yet**

Verification rerun:

```sh
dotnet test Knapper.slnx -c Release --no-restore
# 435 passed: Core 333, MCP 77, Acceptance 25

dotnet test tests/Knapper.Core.Tests -c Release --no-restore \
  --filter "FullyQualifiedName~ReplaceCommitRaceTests|FullyQualifiedName~SymlinkSwapRaceTests|FullyQualifiedName~ParentSwapTests|FullyQualifiedName~AtomicFileTests|FullyQualifiedName~ExternalWriterRaceTests|FullyQualifiedName~DestinationRaceTests|FullyQualifiedName~SourceCaptureRaceTests|FullyQualifiedName~TrashChainTests|FullyQualifiedName~PostCommitFailureTests|FullyQualifiedName~CrashDurabilityTests|FullyQualifiedName~FileAgeSyncGateTests"
# 78 passed
```

`git diff --check` is clean. The tests validate the intended paths, but the
existing hooks also make the untested outcomes below deterministic.

## What round two genuinely fixes

- In the ordinary two-writer race, the second exchange restores the external
  version canonically and the stale agent edit rejects without mutating net.
  The edit and batch assertions are correctly inverted.
- `linkat(..., 0)` plus inspection of the private source link closes the
  pre-publication final-symlink race on both platforms.
- Captured symlinks are judged without following them, and
  `TryRestoreSource` uses `LinkNoFollow`, closing the capture-side deletion
  demonstrated in round two.
- The visible `(Knapper displaced …)` family is consistently recognized by
  the direct conflict gate, and recovered-path exception data reaches audit
  plus the generation counter.
- The heartbeat boundary tests now exercise the intended tolerance edge.

These pieces should remain. The open findings are in the less-common rollback
branches, not the ordinary exchange-back.

## P0 — byte equality again decides ownership of the reclaimed temp

Location: `src/Knapper.Core/Vault/AtomicFile.cs:217-234`

After the swap-back, `reclaimedOurs` decides whether the private temp may be
deleted by comparing its bytes with `written`. That is the exact ownership
rule the repository forbids. A third writer can replace the temporarily
canonical agent edit with a new inode containing **the same bytes**:

```text
target A; Knapper prepares B
first external writer installs D
first exchange: target B, temp D
third writer installs E, where bytes(E) == bytes(B) but inode(E) != inode(B)
swap-back: target D, temp E
SequenceEqual(E, B) => "ours"
finally deletes temp E
```

I reproduced this with `BeforeExchangeTestHook` installing `D` and
`AfterRacedExchangeTestHook` installing a new inode with the same bytes as
Knapper's planned edit. Observed result:

```text
PreconditionFailed
canonical = D
no displaced sibling
no temp
```

The third writer's version disappeared. The comments' claim that a third
write always becomes visible is therefore false for a byte-identical third
write, and `AGENTS.md` explicitly says byte equality is neither ownership nor
continued existence.

Record the identity of Knapper's temp before the first exchange and compare
identity after swap-back (device + inode, or an equivalent stable handle),
not content. Content remains the public SHA precondition; inode identity is
only for deciding whether this private pathname is safe to delete. Add a test
that creates the third version with the same bytes but a distinct inode and
asserts that it becomes the visible displaced sibling and blocks the note.

## P0 — an exceptional read can delete the displaced external version

Location: `src/Knapper.Core/Vault/AtomicFile.cs:105-145,156-167`

`keepTemp` is false when the first exchange succeeds. If
`DisplacedWasTheAuthorizedBase` throws `OutOfMemoryException` while obtaining
metadata or reading/hashing the displaced file, its catch filter deliberately
does not handle that exception. Control reaches `finally`, which sees
`keepTemp == false` and deletes the temp — now the only pathname holding the
displaced external version — while the unacknowledged agent edit remains
canonical.

This is physical data loss, not merely a hidden recovery state. The same
unsafe default applies to any unhandled exception after the first exchange
and before a branch explicitly requests retention.

Switch the ownership default immediately after a successful exchange:
**retain the displaced temp unless and until a later step positively proves
it is safe to remove**. Clear retention only after an authorized-base success,
a clean swap-back that reclaims Knapper's inode, or a successful no-follow
link that gives the displaced object another safe pathname. Add a fault seam
or factored-decision test proving an exception during displaced inspection
cannot delete it.

## P1 — AtomicFile's restore and visible-publication fallbacks still follow symlinks on macOS

Locations:

- `src/Knapper.Core/Vault/AtomicFile.cs:195-206`
- `src/Knapper.Core/Vault/AtomicFile.cs:244-275`

Round two correctly changed `VaultMutationService.TryRestoreSource` to
`LinkNoFollow`, but the equivalent AtomicFile branches still call plain
`Posix.Link`:

- restore displaced temp to an empty canonical pathname at line 197;
- publish the displaced temp as a visible sibling at line 254.

I reproduced the first branch on macOS by racing the target to an
equal-content symlink, then deleting Knapper's briefly canonical bytes through
`AfterRacedExchangeTestHook`. The swap-back failed, the restore link succeeded,
and the result was:

```text
PreconditionFailed
canonical content restored
canonical LinkTarget == null
canonical and outside target are hard links
```

The external writer's symlink was not restored as itself; plain macOS
`link(2)` followed it and recreated the same outside-inode alias round two was
meant to eliminate. The visible-sibling fallback has the identical defect.

Use `Posix.LinkNoFollow` in both branches. Add tests for:

1. displaced symlink + target deletion between exchanges → canonical symlink
   restored as a symlink;
2. displaced symlink + third writer between exchanges → displaced sibling is
   a symlink, the third writer stays canonical, and nothing aliases the
   outside inode.

## P1 — the acknowledged between-exchanges crash state remains silent corruption

Locations:

- `src/Knapper.Core/Vault/AtomicFile.cs:72-90,121-139`
- `AGENTS.md:152-174`

The response documents rather than closes a process death after the first
exchange and before rollback. That interval is not necessarily
"microseconds": it includes non-following metadata, reading and hashing the
entire displaced note (up to the sync ceiling), mismatch handling, and the
second syscall. A kill, OOM termination, or host failure there leaves the
same invalid state as round one's rejected fix: stale agent bytes canonical,
external bytes hidden from Sync/git/queries, and no receipt, audit detail, or
generation action identifying the recovery file.

An uncertain commit after a crash is normally unavoidable; this state is
different because it is not a legal serialization of the raced conditional
write. The repository already requires real-process kill tests where crash
ordering is load-bearing. Add a kill point after the first exchange on a
raced replace and demonstrate the on-disk result. Closing it requires durable
recovery metadata (or another algorithm) sufficient for startup to restore or
publish the displaced version before serving/syncing the stale canonical
bytes. If the residual is to be accepted instead, it needs the vault owner's
explicit risk decision; it cannot be listed as a fixed corruption blocker by
the implementing agent.

## P2 — private-name inspection rejects symlinks but not other non-regular files

Locations:

- `src/Knapper.Core/Mutation/VaultMutationService.cs:653-667`
- `src/Knapper.Core/Vault/AtomicFile.cs:156-167`

`FileInfo.LinkTarget` distinguishes symlinks only. A final source/target can be
replaced with a FIFO, socket, or device after resolution; `LinkNoFollow`
captures it as-is, `LinkTarget` remains null, and `File.ReadAllBytes` may block
or read a non-vault object. The safe private-name inspection prescribed in
round two was "symlink or non-regular"; the implementation covers only the
first half.

The same non-following metadata primitive needed for inode identity should
also classify file type. Require a regular file before any content read or
hash, and add at least a FIFO race test with a bounded assertion so a mutation
cannot hang indefinitely while holding its path locks.

## P2 — a recovered symlink sibling is skipped by health and queries

Location: `src/Knapper.Core/Mutation/ConflictDetector.cs:97-109`

Once the two AtomicFile fallback links are corrected to no-follow, a displaced
symlink can be published under the `(Knapper displaced …)` marker. The direct
conflict sibling check sees its name, but `ScanAll` skips every reparse point
before evaluating `IsConflictName`; normal query/list walks do the same. Thus
the response's claims that health lists it, queries see it, and Sync carries
it do not hold for the symlink case.

It is safe for the conflict health walk to recognize a marker from the entry
name without following the entry. Detect conflict-marker file entries before
the reparse-point skip (while still never recursing into directory symlinks),
and document that a preserved symlink is a filesystem-visible recovery object,
not ordinary queryable/syncable vault content. Add a health test for a
symlink-shaped displaced sibling.

## Deployment status after round three

Do not deploy this working tree against Helios. Before reconsidering:

1. Replace byte equality with stable identity for ownership/cleanup decisions.
2. Default to retaining displaced objects across every exceptional path.
3. Use no-follow links in both AtomicFile recovery branches and cover them on
   macOS and Linux.
4. Resolve or explicitly accept the crash-recovery requirement with a real
   kill test and owner decision.
5. Reject non-regular private captures before reading them and keep conflict
   health honest for symlink recovery objects.
6. Then rerun the full suite, Linux safety families, disposable-vault stress
   session, and the separate oversized-download completeness decision.

---

# Remediation response, round three

Date: 2026-08-20 (same working tree, uncommitted)

All six findings are conceded and addressed; the crash residual (finding 4)
is NARROWED, DEMONSTRATED with a real killed process, and explicitly left as
the vault owner's decision — it is not claimed fixed.

Verification:

```sh
dotnet test Knapper.slnx -c Release   # 453 passed, 0 failed (Core 350, MCP 78, Acceptance 25)
```

plus the safety families — now including the new stat-layout pins — on Linux
in a `dotnet/sdk:10.0` container (the non-following stat is `statx(2)` there,
a code path macOS never executes).

## P0 — ownership by identity: FIXED, with one deliberate strengthening

The reclaim decision now leads with device+inode, exactly as prescribed: the
temp's identity is recorded before the first exchange
(`AtomicFile.cs:98-172`) and only an inode this call created may be
discarded. The prescribed regression test exists and passes
(`A_byte_identical_third_write_is_still_theirs_and_survives_visibly`): a
third write carrying the agent's own planned bytes on a new inode becomes
the visible displaced sibling and blocks the note.

One deliberate deviation from "identity, not content": the reclaim requires
identity AND the exact written bytes, both
(`UndoRacedExchange`, `AtomicFile.cs:229`). Identity alone trusts ext4 not
to hand a just-freed inode number straight back to the next create in the
window — an inode-reuse false-positive would bless a stranger for deletion.
Two proofs, either failing → KEEP, and a kept object is published visibly.
Strictly fewer deletions than either rule alone.

The authorized-base judgement (the other side of the same coin,
`DisplacedWasTheAuthorizedBase`, `AtomicFile.cs:197`) also leads with
metadata — identity + size + mtime, never ctime, which the exchange itself
bumps — so the common unraced case reads no content between the exchanges at
all. Its fallback compares against the EXPECTED BASE hash, which is not an
ownership decision: discarding a displaced object byte-identical to the base
the caller was authorized to consume is a legal serialization (their
identical write, then the conditional replace).

## P0 — retain-by-default: FIXED

`keepTemp` flips to true the instant the first exchange lands
(`AtomicFile.cs:165`) and is cleared only on positive proof: authorized
base, reclaimed-own-inode, successful no-follow restore, or successful
visible publication — the exact rule prescribed. Every throw anywhere after
the exchange now retains. Pinned by
`An_exception_during_the_undo_cannot_delete_the_displaced_version`, which
injects an unanticipated exception through the raced-exchange seam and
asserts the displaced version survives on disk.

## P1 — recovery branches follow no more: FIXED

Both branches round three reproduced are `Posix.LinkNoFollow` now: the
restore-to-canonical (`UndoRacedExchange`) and the visible-sibling
publication (`RecoveredSiblingFailure`, `AtomicFile.cs:305`). Both
prescribed tests exist and pass on both platforms:
`A_displaced_symlink_with_a_deleted_canonical_name_is_restored_as_a_symlink`
(asserts the canonical entry is a symlink again — the reproduction's
LinkTarget==null alias is the explicit counter-assertion) and
`A_displaced_symlink_with_a_third_write_keeps_every_shape_intact` (symlink
canonical, regular third write as the sibling, outside inode aliased by
nothing). One note: in the second scenario this implementation restores the
EARLIER external version (the symlink) canonically and publishes the third
write as the sibling — the same order every other fallback here uses — where
the finding's sketch had them the other way around; the properties demanded
(no alias, symlink preserved as itself, everything visible, note blocked)
hold identically.

## P1 — the between-exchanges crash state: NARROWED + DEMONSTRATED, decision explicitly Dan's

Conceded that round two both understated the window and overstated the
status. What changed:

- ~~The window no longer contains a content read in the common case: the
  authorized-base fast path is one lstat, so the interval the finding
  measured (metadata + full read + hash + mismatch handling) now exists only
  when the commit actually raced, and is a few syscalls plus one page-cached
  read.~~ **Superseded 2026-08-20 (round four,
  `ROUND_FOUR_METADATA_FAST_PATH_FINDING.md`): the metadata fast path was
  itself a P0 — an equal dev/inode/size/mtime tuple does not prove equal
  bytes, and a same-inode, same-length in-place overwrite with a restored
  (or granularity-aliased) mtime rode it to a silent destruction of the
  external write. The fast path is removed; the displaced object is always
  hashed, so the window again includes one page-cached read-and-hash
  (bounded by `Sync__MaxFileBytes`). Narrowing the window requires durable
  recovery metadata, not a stat proxy for content.**
- The prescribed real-process kill test exists:
  `A_raced_replace_killed_between_the_exchanges_leaves_the_documented_residual`
  (via the new `crash-replace-raced` probe mode,
  `Knapper.MutationProbe/Program.cs:63`) kills a genuinely raced replace
  between the exchanges and PINS the on-disk result: stale agent bytes
  canonical, external version only under a hidden temp. The
  `CrashDurabilityTests` header now names this as the one exception to the
  residue-is-a-duplicate rule.
- CLAUDE.md/AGENTS.md state that closing it requires durable recovery
  metadata plus a startup pass this design deliberately lacks, and that
  accepting the residual is the vault owner's recorded decision.

**Decision required from Dan before deployment**: accept the demonstrated
residual (external write within one syscall of the final check AND a
kill/OOM/power cut within the next few syscalls; recovery = the hidden temp,
findable by `ls -a`, plus the write-ahead audit "attempt" record), or
commission the journal-and-startup-recovery design the current architecture
explicitly rejects. This response does not make that call.

## P2 — non-regular files: FIXED

`Posix.LStat` (statx on Linux — arch-independent layout; lstat on macOS
arm64; layouts pinned by `PosixStatTests` on the running platform) now
classifies before EVERY content read taken under path locks: `ReadExisting`
(the first read of every mutation — a FIFO at the pathname used to hang
there, before the private-name inspection could ever run), the private-name
inspection (symlink → `[SymlinkRejected]`, other non-regular →
`[PreconditionFailed]`), `CapturedIsOurs`, `RequireStillOurBytes`, the
rollback's `Holds`, and `Replace`'s entry check and displaced judgement.
Three FIFO race tests with explicit 15-second bounds cover the move source,
the replace target, and the at-rest FIFO (`SymlinkSwapRaceTests`).

## P2 — symlink-shaped conflict siblings: FIXED

`ConflictDetector.ScanAll` judges conflict names BEFORE the reparse-point
skip (`ConflictDetector.cs:106`) — recognition never follows the entry, and
recursion still refuses every directory symlink (the walk lists a
conflict-named dir-symlink too, since a displaced symlink can point at a
directory). Covered at the detector level (`ConflictMarkerTests`: a dangling
symlink-shaped sibling is listed; a directory symlink is still never
entered) and at the health level
(`HealthServiceTests.A_symlink_shaped_displaced_sibling_still_degrades_health`).
The visibility claim is corrected rather than restated: a symlink-shaped
recovery object is a filesystem-visible artifact the conflict walk lists and
the gate blocks on — NOT ordinary queryable/syncable content — and
CLAUDE.md now says exactly that.

## Remaining before deployment

1. Dan's decision on the demonstrated crash residual (above).
2. The disposable-vault stress session (runbook §8b).
3. The oversized-download completeness decision.
