# Round-four production safety finding: metadata is not a content precondition

**Status:** Remediated 2026-08-20 — fast path removed; the displaced object
is always judged by bytes (`AtomicFile.DisplacedWasTheAuthorizedBase`).
`Posix.StatInfo.SameIdentityAndContentStamp` is deleted outright rather than
left as a dead primitive; `SameIdentity` (the cleanup-ownership question)
remains. Regression tests added to `ReplaceCommitRaceTests`: the in-place
restored-mtime repro, the new-inode restored-size-and-mtime variant, and an
unreadable-displaced-base pin (an external chmod bumps only ctime, so the
stamp called it untouched and succeeded; it now routes to the swap-back — a
spurious but safe `PreconditionFailed`, the deliberate price of judging by
bytes alone). All three were run against a temporarily reinstated fast path:
the repro and the chmod pin FAIL against it, proving their teeth; the
new-inode variant passes either way, as expected (identity already fails it)
— it pins the identity half against future promotion into sufficiency.
Docs updated to match (CLAUDE.md/AGENTS.md, `docs/architecture.md`, the
superseded fast-path bullet in `HEAD_COMMIT_PRODUCTION_SAFETY_REVIEW.md`,
and the crash-window description everywhere it appeared). Full suite green
on macOS (456 tests, acceptance tier included); Linux rerun is CI's on the
next push.  
**Severity:** P0 — silent destruction of a raced external write while reporting success  
**Deployment impact:** Do not deploy the current working tree against Helios until this is fixed and regression-tested.  
**Reviewed tree:** uncommitted remediation on `ca95968` (2026-08-20)

## Finding

`AtomicFile.Replace` now treats an unchanged non-following metadata tuple as
proof that the file displaced by the atomic exchange is still the base whose
SHA-256 the caller authorized:

- `src/Knapper.Core/Vault/AtomicFile.cs:106` records the pre-read `LStat`.
- `src/Knapper.Core/Vault/AtomicFile.cs:122` reads and hashes the expected base.
- `src/Knapper.Core/Vault/AtomicFile.cs:144` performs the exchange.
- `src/Knapper.Core/Vault/AtomicFile.cs:203-204` accepts the displaced object
  without reading it when `SameIdentityAndContentStamp(before)` is true.
- `src/Knapper.Core/Interop/Posix.cs:312-314` defines that stamp as device,
  inode, size, and mtime.

That tuple is useful evidence, but it is not proof that content is unchanged.
An in-place writer can change bytes without changing device, inode, or size and
can preserve or restore mtime. Filesystem timestamp granularity can create the
same ambiguity without an explicit `utimensat`/`touch` restoration. Once the
tuple compares equal, Knapper skips the SHA check, accepts the stale commit,
deletes the displaced external bytes with the temp, and reports success.

This violates the central mutation contract: the last-instant SHA precondition
must be judged against the bytes actually displaced by the commit. Metadata
cannot substitute for that byte-level proof.

## Deterministic reproduction

The reproduction used the existing `AtomicFile.BeforeExchangeTestHook`, after
the courtesy SHA read and immediately before the exchange:

1. Create `note.md` containing ten `A` bytes.
2. Set its mtime to a fixed whole-second value.
3. Call `AtomicFile.Replace`, expecting the SHA-256 of the `A` bytes and
   proposing ten `C` bytes.
4. In `BeforeExchangeTestHook`, overwrite the same inode in place with ten `B`
   bytes and restore the fixed mtime.
5. Let the exchange and metadata fast path run normally.

Observed output on macOS:

```text
result=success
canonical=CCCCCCCCCC
entries=note.md
```

The external writer's `BBBBBBBBBB` version was the object displaced by the
exchange. It matched device, inode, size, and mtime, so it was classified as
the authorized `AAAAAAAAAA` base without hashing. The temp was then deleted.
No rejection, recovery sibling, or hidden survivor remained.

The same logic is platform-independent; Linux `statx` supplies the same fields
to the same comparison.

## Why the current tests do not catch it

`PosixStatTests.The_content_stamp_moves_when_the_file_is_written_in_place`
demonstrates only the ordinary case: it sleeps, appends data, and expects mtime
or size to change. It does not establish the inverse proposition that an equal
stamp implies equal bytes. That inverse is false.

The stat-layout tests validate that Knapper reads the intended fields. They
cannot validate that those fields are a safe replacement for a content hash.

## Required remediation

Remove `SameIdentityAndContentStamp` as an acceptance path for the displaced
authorized base. After the first exchange, accept and discard the displaced
object only after all of the following are true:

1. Its final component is inspected without following symlinks.
2. It is a regular file.
3. Its bytes hash to `expectedSha256`.

Device/inode identity remains appropriate for the separate cleanup-ownership
question in `UndoRacedExchange`; it does not establish content equality.

This restores the content read between the two exchanges in the raced-replace
algorithm. Consequently, the documented crash window must again be described
as including a read and hash of up to `Sync__MaxFileBytes`, rather than as "one
lstat wide" in the common path. Optimizing that window cannot weaken the
precondition that prevents silent loss.

If reducing the crash window remains a goal, it requires a different design
that preserves byte-level proof—most likely durable recovery metadata—rather
than a metadata proxy for content.

## Regression tests required

Add a deterministic test to `ReplaceCommitRaceTests` that:

1. fixes the original file's mtime;
2. overwrites it in place with different, equal-length bytes in
   `BeforeExchangeTestHook`;
3. restores the original mtime;
4. asserts `PreconditionFailed`;
5. asserts the external bytes are restored canonically;
6. asserts Knapper's proposed bytes are not canonical and no external version
   is deleted.

Add a second variant that replaces the pathname with a new inode but restores
the same size and mtime. It should take the same reject-and-restore path. The
test should not rely on inode reuse.

The full Release suite and focused mutation-safety suite should then be rerun
on macOS and Linux.

## Review boundary

This note records this P0 only. It does not accept the separately documented
crash residual, the oversized-download completeness hole, or the outstanding
runbook §8b disposable-vault stress session.
