# Proposal: upload grants through inherited MCP authentication

Status: implementation handoff, not built. Written 2026-09-05 (UTC).
The implementation baseline below resolves draft choices; live deployment
verification remains a separate gate. **Read Amendment 1 at the end first** —
it takes precedence over both the body and the baseline, and it changes the
scoping rule, the replacement default, and the order of the gates.

This follows [bulk-ingest.md](bulk-ingest.md). Dan's clarified use case is
recurring 20–100 KB scripts already saved in agents' workspaces; preserving
the authentication inherited from the cloud MCP connector is important.
The same transport should carry binary attachments. No production settings
were inspected or changed for this review.

## Recommendation

Use the existing authenticated MCP connection to authorize an upload and
to commit it. Between those calls, a local helper transfers raw bytes using
a short-lived upload capability. The helper needs no Cloudflare service
token, OAuth refresh token, SSH key, or direct vault access.

The capability authorizes filling one temporary buffer with one exact
payload. It does not authorize a vault mutation. A final authenticated MCP
call runs the mutation through `VaultMutationService`.

This costs two small MCP calls and one HTTP transfer in the normal case.
An existing-file replacement additionally needs the usual fresh read/stat.
The payload never needs to pass through model output again. File size
affects transfer time and memory, rather than conversation size.

```mermaid
sequenceDiagram
    participant A as Agent
    participant M as Inherited MCP connection
    participant K as Knapper
    participant H as Local upload helper
    A->>H: Inspect local file: size and SHA-256
    H-->>A: Small metadata result
    A->>M: Prepare upload: destination, hash, size, precondition
    M->>K: Authenticated MCP call
    K-->>A: Upload ID, endpoint, temporary token, expiry
    A->>H: Send local file using grant
    H->>K: PUT raw bytes with upload token
    K-->>H: Staged, hash checked; no vault write
    H-->>A: Small transfer receipt
    A->>M: Commit upload ID
    M->>K: Authenticated MCP call
    K->>K: Fresh precondition, gates, locks, AtomicFile, verify, audit
    K-->>A: Mutation receipt
```

The extra commit call is deliberate. Committing directly inside PUT would
save one MCP round trip, but would give the bearer token vault-write
authority and require the public upload endpoint to handle mutations,
attribution, and ambiguous write retries. Finalizing through MCP preserves
the existing authenticated mutation boundary and checks access again under
the normal Access policy and token-lifetime rules. It does not promise
instantaneous revocation beyond the existing authentication system.

## Protocol

Use the names and fields below for implementation. They become a locked
client contract when shipped. The implementation baseline at the end pins
wire responses, defaults, and edge-case behavior.

### 1. Prepare through MCP

```text
vault_upload_prepare(
    path,
    sourceSha256,
    byteLength,
    mode: "create" | "replace",
    expectSha256?: string
)
```

`create` explicitly means expect absent; reject a supplied `expectSha256`.
`replace` requires a nonempty expected destination hash and refuses a
missing file. Never infer an upsert, fall back to create, or obtain a newer
hash automatically. Whole-file replacement remains disabled by default,
as required by brief §7; enabling it is a separate deployment decision.
Create-only uploads already address storing new scripts and attachments.

Validate the destination through `VaultPathResolver`, validate the source
hash and integer byte count, and reserve bounded capacity. Early checks of
destination state and gates are advisory; they neither reserve the path nor
replace the checks at commit. Parent directories must already exist.

The server records immutable authorization metadata plus synchronized state:

- a random upload ID and a hash of a separately generated 256-bit token;
- the verified issuing principal, issuing request ID, and attribution;
- the destination, operation, original destination precondition;
- exact expected source length and SHA-256;
- an expiry and the current upload state.

Return a configured HTTPS upload endpoint, the upload ID, token, expiry,
and an echo of the bound metadata. Do not derive the endpoint from arbitrary
Host or forwarded headers. The client verifies the echo before transferring.

Authorization identity must come from validated Access claims. Define and
test a stable principal key for supported owner credentials; do not reuse
the display-oriented `ToolSupport.Caller().Client` string as the security
key. Missing required claims must fail closed. Do not bind to a particular
MCP session or access-token string: reconnects and token refreshes are normal.
The client application's self-reported name is attribution, not identity.

### 2. Transfer over HTTPS

```text
PUT /uploads/{uploadId}
Authorization: KnapperUpload <temporary-token>
Content-Type: application/octet-stream

<raw file bytes>
```

The server authenticates the grant before application-level body buffering,
atomically claims it, reads a bounded body, and compares its actual length
and SHA-256 against the grant. The URL ID is not a credential. Neither a
destination nor a new precondition can be supplied by this request.

The payload is opaque: no UTF-8 conversion, BOM removal, newline changes,
base64, decompression, archive extraction, execution, or MIME-driven
processing. Reject content encodings requiring transformation. Enforce the
actual streamed byte count even when Content-Length is missing or false.
Reject excess bytes and truncated uploads; never stage a matching prefix of
a longer body. Apply request-size limits and timeouts before parsing or
buffering large bodies.

A successful response means only `staged`, with length and hash. It must
not use the mutation receipt's `Verified` terminology or claim Sync delivery.
There is no download/list endpoint and no file content in an error response.

### 3. Commit through MCP

```text
vault_upload_commit(uploadId)
```

Require ordinary owner authentication and the same verified principal that
prepared the upload. The ID alone is insufficient. The server checks that
the upload is ready and unexpired and atomically claims it for commit.
Commit takes no alternative path, replacement bytes, or updated hash.

Re-resolve the destination now. Feed the owned, immutable payload snapshot
through a byte-oriented entry point in `VaultMutationService`, retaining
the existing gates, locks, fresh target read/hash check, size guard,
write-ahead audit, atomic commit, containment checks, and byte verification.
No network read occurs while holding vault locks. Preserve existing-file
mode; use the normal create mode for a new file.

Return the ordinary mutation receipt plus upload correlation. The receipt's
new hash must equal the source hash bound in the grant. This extends fidelity
from the client's local snapshot through the received bytes to the verified
vault bytes. It proves transfer fidelity, not that the script is correct.

### 4. Status through MCP when needed

```text
vault_upload_status(uploadId)
```

Use the same owner/principal checks. Return state and, when available, the
stored terminal outcome. Never return the token or bytes. Status is separate
from commit so a caller checking an uncertain transfer cannot accidentally
start a vault mutation. No cancellation tool is required initially; expiry
reclaims abandoned uploads.

## Single use, retries, and crashes

The state machine is:

```text
issued -> receiving -> ready -> committing -> succeeded | failed
                    -> failed
issued / ready -> expired
receiving -> failed on disconnect, timeout, or expiry
```

One authenticated PUT attempt claims `issued` atomically. Concurrent
attempts cannot both receive a buffer. A failed or interrupted transfer
burns that grant; request a new one. At these sizes, restarting transfer is
preferable to resumable chunks and partial-file state. A repeated PUT after
acceptance never changes staged bytes. Its response directs the caller to
authenticated status; invalid credentials always receive a generic refusal.

A lost PUT response is resolved by MCP status: ready means proceed to
commit, receiving means wait, failed/expired means prepare again. Expiry
uses monotonic elapsed time internally; return wall-clock expiry as client
guidance. Receiving has its own short absolute deadline.

Concurrent commits for one ID cannot both call the mutation service. While
committing, return an in-progress state to a duplicate call; after completion,
return the cached outcome without writing again. Retrying a completed commit
does not assert that its historical result is still the vault's current state.

After a mutation attempt fails, the grant is terminal. Do not automatically
retry the mutation, even after an I/O failure: some existing failure paths
can have visible side effects or conflict recovery siblings. Report the
original typed error and recovery information. A stale precondition needs
a fresh read and an explicitly rebuilt operation, never a replacement hash
silently substituted into the grant.

An initial implementation can keep bounded payloads, grants, and terminal
receipts in memory in the single serving process. A restart invalidates all
grants and frees staging; it cannot replay one. Route all phases to that
process. Another instance must reject an unknown ID, never reconstruct it
from a self-contained token. Multiple interchangeable workers would require
shared state or explicit routing and are outside this initial design.

The honest restart limitation is a commit that landed before the process
died or its receipt was delivered. An unknown ID is not proof that no write
occurred. Use fresh `vault_stat` plus audit correlation to investigate; a
matching hash establishes current content, not historical causation. Never
automatically mint a replacement using the current destination hash. This
design does not provide durable exactly-once receipts, nor close the
existing `AtomicFile.Replace` crash window. Doing that needs durable
recovery design, not a JWT or an extra in-memory flag.

## Cloudflare and origin routing

The current Access application would refuse the helper: a Knapper grant is
not a Cloudflare OAuth token. The transfer route therefore needs its own
Access configuration. Cloudflare documents a separate, path-scoped Bypass
application for this purpose; more-specific application paths override the
parent's policy rather than inheriting it.
Sources: [path-scoped Bypass](https://developers.cloudflare.com/cloudflare-one/access-controls/policies/common-policies/#bypass-a-public-endpoint),
[application precedence](https://developers.cloudflare.com/cloudflare-one/access-controls/policies/app-paths/#policy-inheritance).

Proposed initial topology: retain the existing hostname and owner policy,
with a tightly scoped exception for `/uploads/*`. Knapper protects the exact
PUT route with its own upload-grant authentication scheme. This endpoint
is publicly reachable, but receiving bytes still requires a valid grant.
Access and origin owner authentication still protect every MCP call.

Use a positive endpoint authorization policy, not a general "skip auth when
path starts with uploads" middleware rule. `HostGuard` remains in front of
all routes. Upload auth has no loopback exemption, accepts no Access token
as a substitute for a grant, and is never an alternative way to authenticate
to MCP or `/up`. Keep the existing Access assertion handler's deliberate
separation from Authorization. No global authentication disable or broad
anonymous route group.

Neither browser cookies nor CORS are required for the local helper. Preserve
Origin checks, reject methods other than the specified PUT, send no-store
responses, and disable redirects in the helper. Keep the capability in a
header, never a URL query/path. The nonsecret upload ID can appear in logs.

The actual Access application type, path matching, tunnel routing, edge
limits, and deployed hostname must be checked before implementation is
declared deployable. Test the exception and adjacent paths through the
real tunnel in the disposable-vault environment. The production runbook
describes a service-token Code client, while Dan reports inherited cloud
authentication; do not treat that historical recipe as verified live state.

## Resource bounds and secret handling

Initial values, to be validated against the deployment's memory before rollout:

| Limit | Proposed value |
|---|---|
| File bytes | At most `Sync__MaxFileBytes` (currently defaults to 5,000,000) |
| Grant lifetime including ready time | 10 minutes |
| Receiving deadline | 60 seconds, also bounded by grant expiry |
| Active grants | 8 per process, 4 per principal |
| Reserved payload bytes | 32 MiB total across all active states |
| Terminal receipt retention | 15 minutes; 128 total active/terminal metadata slots |

Reserve both a slot and declared bytes at prepare. Count receiving, ready,
and committing buffers against capacity; zero-byte files still use a slot.
Free buffers on failure, expiry, or completed commit. Never evict or recycle
a buffer being used by a receiver or committer; expiry may prevent a commit
from starting but cannot cancel cleanup or verification after it starts.
Bound receive concurrency separately and account for temporary copies in
the memory budget. Prefer an exact-size buffer over unbounded stream copies.

Memory staging avoids disk paths, spool cleanup, source symlink races, and
new durable state. On restart the local source file remains the retry source.
Do not promise secure erasure of managed memory. If larger payloads later
need disk staging, specify a server-owned directory outside the vault,
private permissions, no-follow access, bounds, and owned-file cleanup then.

The token is a secret, although not a permanent account credential. It
necessarily passes through a small MCP result and may remain in the client
transcript. Scope and expiry limit its authority: possession permits only
the exact declared payload, and does not permit commit, download, or access
to any other grant. A leak can still burn the grant and deny that transfer.

The helper accepts the grant through stdin or an owner-only temporary file,
does not put it in command-line arguments, and prints no token or file body.
Redact authorization headers in application/proxy diagnostics and do not log
MCP grant responses server-side. Rate-limit invalid attempts and bound their
logs; do not fsync an audit record for every unauthenticated Internet probe.
Authenticate before allocating application payload capacity. Edge and
connection-level resource controls are still needed for public traffic.

## Text, binary, and the Core change

Both are the same opaque byte transfer. In particular, preserve UTF-8 BOMs,
CRLF, zero bytes, and arbitrary non-UTF-8 data. No text-only validation in
the upload writer, and no new binary download surface is implied.

`AtomicFile` already accepts byte arrays. `VaultMutationService.Create`
currently takes text, and `Mutate` is private. Refactor the shared create
critical section so existing text create delegates to a byte-oriented create
path. Add conditional whole-file replacement through the existing `Mutate`
discipline only if replacement is enabled; never expose an unconditional
Core replacement helper. Keep existing edit/append UTF-8 requirements.

The server-owned buffer must not remain writable by a concurrent upload
handler after acceptance. The bytes hashed, committed, and described by the
receipt must be the same snapshot, not multiple reads of a mutable source.

`vault_stat` already hashes binary files, including files above the read
body cap. It supplies a replacement precondition without returning content.
`vault_read` continues to reject non-UTF-8 content. Successful local byte
verification does not prove Sync propagation; validate Sync file-type
settings for scripts and attachments on the replicas as well as the size
ceiling. The git secret scanner and conflict rules continue to apply.

## Implementation and evidence required

The main additions belong in Mcp: a grant store, explicit transfer endpoint
auth, bounded receiver, and typed prepare/commit/status tools. Core gets the
shared byte mutation path; Cli gets a transport helper that uses the grant
instead of opening its own authenticated MCP connection. A small agent
instruction documents the metadata -> prepare -> transfer -> commit loop.

Link preparation, transfer outcome, and mutation audit records with a
nonsecret upload ID and authenticated principal. Preserve issuing and commit
request IDs. Before any vault write, Core's existing fsynced audit intent
must succeed. Do not log uploaded bytes, tokens, or content-bearing errors.
Expose bounded upload capacity/expiry/failure metrics; do not turn one
failed client transfer into a vault-wide unhealthy status.

Before shipping, prove:

- Exact-byte text and binary transfers at 20 KB, 100 KB, zero bytes, and
  the configured ceiling; BOM, CRLF, invalid UTF-8, and changed local files.
- Missing/invalid/expired grants, incorrect size/hash, excess streamed bytes,
  disconnects, slow clients, capacity exhaustion, and cleanup under races.
- One receiver and one mutation attempt per grant under concurrent requests;
  duplicate calls return historical outcomes without repeating writes.
- No vault change on staging, another principal cannot inspect or finalize,
  and grant tokens cannot authorize MCP, reads, or monitoring endpoints.
- Create/no-clobber and replacement/stale-hash races through the byte entry
  point, with real second processes; preserve existing external-writer,
  containment, conflict, sync-gate, audit-failure, and short-write behavior.
- Process death during receive, ready, and commit produces the documented
  invalidation and uncertain-outcome behavior, with no automatic replay.
- Raw tools/list schemas and structured responses conform; the three tool
  lists stay aligned, with concrete return types and ToolSupport.Run.
- Feature disablement denies prepare, transfer, and commit consistently;
  hiding an MCP tool must not leave a usable transfer or commit route behind.
- Real tunnel tests cover the exact exception, malformed/adjacent paths,
  wrong Host/Origin/method, and all existing owner/monitoring refusals.

Extend read-only `knapper verify` with non-mutating refusal probes. Valid
transfer and commit tests belong to the disposable-vault acceptance/smoke
environment, never the production verify command. Use explicit expected
statuses and error shapes, never "not 200" or followed login redirects.

This is a minor release: new tools, errors, and configuration are client
contracts. No server code, upload endpoint, or Cloudflare exception exists
as a result of this document.

## Implementation baseline and handoff

This section takes precedence over tentative wording above. Implement and
test the feature locally without changing production, releasing, or enabling
the Cloudflare exception. Those actions require their own deployment task.
Ordinary implementation choices such as class/file names do not require a
new design decision; departures from authorization, mutation, or retry
semantics must be identified before making them.

### Scope and precedence

Implement create and conditional replacement for arbitrary bytes, with
replacement off by default. Preserve all existing text tools and their
contracts. Use memory staging, a stateful opaque token, the existing public
hostname, three MCP tools, and the single PUT endpoint. Do not introduce
SCP, durable staging, signed JWT upload tokens, resumable uploads, a binary
download endpoint, direct-PUT commits, or a second permanent credential.

For this workspace-file workflow, this document supersedes the "build
nothing" recommendation in `bulk-ingest.md` and its summary in
`docs/extending.md`. The implementation must update those cross-references
so agents do not receive two conflicting instructions. Repository
invariants and the requirements brief continue to govern the implementation.

### Wire shapes

Use the existing camelCase wire convention. SHA strings are exactly 64 hex
characters, accepted in either case and normalized to lowercase. Reject
whitespace and malformed hashes as `InvalidArgument`. `byteLength` is a
nonnegative 64-bit integer, including zero; reject oversized values before
allocation. Upload IDs are independently generated 32-byte random values
encoded as unpadded base64url; tokens use the same encoding with independent
random bytes. Compare stored token digests using a fixed-time comparison.

Prepare returns a concrete `UploadGrant`:

```text
uploadId, uploadUrl, uploadToken, expiresAt,
path, sourceSha256, byteLength, mode, expectSha256
```

`expiresAt` is a UTC DateTimeOffset. `expectSha256` is explicitly null in
create mode. `uploadUrl` is the complete URL for this ID. All those fields
are required in the response schema, including the nullable one.

Commit returns a concrete `UploadCommitResult`:

```text
uploadId, state: "committing" | "succeeded", result: MutationResult | null
```

A successful initial commit returns `succeeded` and the receipt. A duplicate
while it is running returns `committing` and null. A cached success returns
the original receipt. Initial or cached failures go through `ToolSupport.Run`
as the original bracketed typed error. Commit of issued/receiving state fails
`UploadNotReady` without consuming it; commit of an expired grant fails
`UploadExpired`. A failed grant never starts another mutation.

Status returns a concrete `UploadStatusResult`:

```text
uploadId, state, expiresAt, path, sourceSha256, byteLength,
result: MutationResult | null,
errorCode: string | null, errorMessage: string | null
```

`state` uses the seven states in the state machine. All listed fields are
required, with explicit nulls. Only succeeded has a result; failed/expired
carry a typed error code and safe message. Issued/receiving/ready/committing
have neither. Do not cache exceptions or HttpContext objects: snapshot the
bounded outcome fields, including recovery-path information when present.
Unknown, evicted, and other-principal IDs all fail `UploadNotFound`; the
response must not reveal which case occurred. Terminal receipt lookup is
allowed after grant expiry until metadata retention ends.

PUT returns HTTP 200 with `{uploadId, state: "ready", byteLength, sha256}`
only after the complete payload passes validation. Application-generated
errors return `{code, message}` with these mappings:

| Condition | HTTP | Code |
|---|---|---|
| Missing/bad credential, unknown or malformed ID | 401 | `UploadInvalidToken` |
| Valid credential for expired grant | 410 | `UploadExpired` |
| Valid credential for already claimed/terminal grant | 409 | `UploadAlreadyClaimed` |
| Declared or actual excess bytes | 413 | `UploadSizeMismatch` |
| Complete but short body | 400 | `UploadSizeMismatch` |
| Wrong payload hash | 400 | `UploadHashMismatch` |
| Unsupported Content-Type or Content-Encoding | 415 | `InvalidArgument` |
| Receive deadline exceeded, when a response is possible | 408 | `UploadTransferTimeout` |
| Receiver capacity temporarily unavailable | 429 | `UploadCapacityExceeded` |
| Feature disabled | 404 | `UploadDisabled` |

Use a `WWW-Authenticate: KnapperUpload` challenge on 401. Do not put a
Cloudflare/RFC 9728 resource-metadata pointer on that challenge. Unsupported
methods are 405 and never claim a grant. Protocol-level malformed HTTP may
be rejected by Kestrel before these application responses are possible.
Disconnects/stream I/O failures record `UploadTransferFailed`; a disconnected
client need not receive an HTTP response. A complete zero-byte body is valid.

Validate the credential and receiver capacity before the atomic claim. A
429 does not claim or burn the token; every admitted authenticated PUT does,
including one subsequently refused for size or content headers. Claimed
receives failing validation become terminal failed. An invalid token must
not burn a valid grant. Accept only `application/octet-stream` and absent
or identity Content-Encoding. Content-Length is optional, but when present
must equal the bound length. Enforce actual length and end-of-body as well.

Add the upload-specific codes above to the shared typed error vocabulary,
plus `UploadNotFound`, `UploadNotReady`, and `UploadReplacementDisabled`.
Use existing mutation codes unchanged for commit failures. HTTP and MCP
must share the upload state/error definitions rather than maintaining two
independent mappings of their meaning.

### Principal identity and configuration

After owner authorization, derive a structured principal key from the
validated issuer and nonempty `sub`: `(issuer, "sub", subject)`. For an
owner service assertion without a subject, accept a nonempty validated
`common_name` as `(issuer, "service", commonName)`. Never use email, clientInfo,
request headers supplied by the helper, or string concatenation with an
ambiguous delimiter. Reject missing/ambiguous identity claims. If fixture
or live owner assertions do not support this rule, report the mismatch;
do not invent an email-based fallback. Pin both credential shapes and
refresh/reconnect continuity in tests.

An owner-policy loopback exemption by itself supplies no issuing principal
and cannot prepare/commit/query uploads. Upload tests must supply fixture
authenticated identities (fake Access edge or test-only authentication),
without adding a production fallback identity. The PUT still accepts its
own capability independently and never exempts loopback.

Add `UploadOptions`, shared by server validation and doctor, under `Upload`:

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | false | Enable the complete feature |
| `AllowReplace` | false | Permit conditional full-file replacement |
| `PublicOrigin` | empty | Exact HTTPS origin used to construct `/uploads/{id}` |
| `GrantLifetimeSeconds` | 600 | Issued-to-commit-start lifetime |
| `ReceiveTimeoutSeconds` | 60 | Absolute receive deadline |
| `MaxActiveGrants` | 8 | Process-wide active limit |
| `MaxActiveGrantsPerPrincipal` | 4 | Active limit for one authenticated principal |
| `MaxBufferedBytes` | 33554432 | Sum of reserved payload sizes |
| `MaxConcurrentReceives` | 4 | Simultaneous admitted receivers |
| `ReceiptRetentionSeconds` | 900 | Terminal-state metadata lifetime |
| `MaxTrackedGrants` | 128 | Active plus retained terminal records |

Use `Sync__MaxFileBytes` as the file ceiling; do not add a second file-size
carrier. Force validation at startup. Numeric limits must be positive,
per-principal and receiver limits cannot exceed active limits, tracked
capacity must cover active capacity, and buffer capacity must accommodate
one maximum permitted file. Use checked arithmetic for reservations.
When enabled, PublicOrigin is required, HTTPS, without userinfo, path other
than `/`, query, or fragment, and its host must satisfy the configured host
allowlist. Reject incoherent enabled configuration in both server and doctor.
Local integration tests can override URL transport at the test boundary;
do not add a public insecure-HTTP option.

Keep the new names in all three locked tool tables even when disabled;
calls fail `UploadDisabled`, and PUT returns the defined 404. If Enabled
is true while any of the three tools is in `Mcp:DisabledTools`, refuse
startup. Treat configuration as fixed for the process; changing it requires
a restart, invalidating grants. Disabling AllowReplace refuses replacement
preparation with `UploadReplacementDisabled`.

Reserve a tracked metadata slot at preparation as well as active capacity.
Do not evict unexpired terminal outcomes to admit new work: return
`UploadCapacityExceeded`. Retention begins when a terminal state is reached,
not each time status is polled. Sweep expiry on a bounded timer and on store
access; a transition cannot free capacity owned by an in-flight operation.
Bound failure messages and metadata so an entry-count cap bounds memory.

### Helper and audit interface

Add two local CLI commands that read no vault configuration and do not
connect as an MCP client:

```text
knapper upload inspect --file <local-path>
knapper upload send --file <local-path> --grant-stdin
```

Inspect prints JSON `{sourceSha256, byteLength}` from one bounded read of a
regular source file. Send reads the grant JSON from stdin, reads the source
into a bounded snapshot, and checks hash and length before making any
request. If the file changed, fail locally and require fresh preparation.
Send the same snapshot that was checked. Limit local input to the grant's
byte count, reject non-regular sources without blocking, and never execute
the source. Do not put payload bytes or tokens on stdout/stderr or argv.

Require a strict HTTPS grant URL, no userinfo/query/fragment, and the exact
`/uploads/{uploadId}` path. The helper must receive grants from the trusted
MCP tool result, not from note content. Disable redirects and automatic
HTTP retries. Output the small staging receipt on success; use nonzero
exit status with bounded structured errors otherwise. An uncertain network
outcome directs the agent to MCP status, not a blind re-upload. Creating a
private grant file from the small tool result and piping it to stdin is an
acceptable client workflow; clean that owned file after use.

Extend AuditContext and AuditLog.Entry with optional upload correlation and
issuing-request fields, preserving existing callers/serialization. Capture
the authenticated commit request ID through the normal caller path. Use
these fields for Core's intent and terminal records, rather than replacing
the request ID with an upload ID. Preparation must durably audit before
returning a usable grant; roll back its reservations if that append fails.
Transfer outcome auditing is best-effort with the existing durable audit
failure signal; it cannot authorize a vault write. Core's commit intent
remains fail-closed. Record only IDs, identities, sizes/hashes and safe error
codes; no payload or token. No disk write for unauthenticated probes.

### Completion gates

Implementation is complete when the code, helper, agent usage instructions,
configuration/error reference, proposal cross-references, and tests are
updated; `dotnet build Knapper.slnx` and `dotnet test Knapper.slnx` pass; and
the new helper-to-real-server path passes against a disposable vault with
text and binary payloads. Include Core race, raw wire schema/conformance,
and acceptance coverage listed above. Add release packaging for a macOS
helper usable from Dan's agent workspaces: the current production Linux
tarball alone does not deliver that client. Preserve the sole version
carrier and reproducible artifact conventions. Document the supported
helper installation and update path without cutting a release in this task.

Deployment is separately complete only after verifying the real Access
principal shapes and inherited connector behavior; configuring and testing
the narrow edge exception on a disposable deployment; proving valid
prepare/transfer/commit from an actual desktop agent without separate auth;
checking text/binary Sync propagation; and completing the normal release,
clean-tag publication, production runbook, and read-only verify gates.
These are external acceptance checks, not reasons to stop local implementation.

## Amendment 1 — scope, replacement, and gate order (2026-09-05)

Precedence: where this section differs from the body or from the
implementation baseline, this section governs. Nothing else is withdrawn; the
protocol, resource bounds, wire shapes, and evidence list all stand.

Written a few hours after the body, following the first real occurrence of the
problem it addresses. The occurrence produced evidence the draft did not have.

### The occurrence

`Tech/Homelab/Proxmox/knapper-deploy.sh` was updated in place through
`vault_edit`: 20,250 → 26,618 bytes. The content existed as a file on the
agent's local disk, which is exactly the shape this proposal targets. It cost
roughly 7 KB of anchored-edit text rather than the ~47 KB a whole-file round
trip would have needed, because the vault copy was byte-identical to a local
pre-edit baseline and the change could therefore be expressed as seven hunks.
The result was proved rather than assumed: the returned `newSha256` equalled
the local file's SHA-256 exactly. No upload path existed and none was needed.

### A. The discriminator is the BASE, not the size

The body frames the problem as payload size — "recurring 20–100 KB scripts".
Size is not the discriminator. Possession of the destination's current bytes
is.

When the base is held locally, an anchored `vault_edit` sends only the changed
regions, keeps the SHA precondition and the guard discipline, and returns a
receipt that proves the result by hash. That is cheaper than an upload at
every size where the diff is small relative to the file, which is the normal
case for a script under revision.

Upload is the right transport when, and only when, one of these holds:

- the file is new (no base exists);
- the payload is not UTF-8 text;
- the change is a wholesale rewrite, so the diff approaches the file;
- **the caller does not hold the current bytes** — a different machine, a
  different agent, or a file that only the vault has.

Implementation consequence, binding on the agent instructions required by
"Implementation and evidence required": state the base test, never a size
threshold. An instruction reading "use uploads for large files" routes
evolving text onto the upload path, where it costs more, gives up anchored
guards, and burns a grant. `vault_edit` remains the first choice for text
whose base the caller holds, at any size.

### B. Replacement is the recurring case — decide `AllowReplace` before building

The baseline ships `AllowReplace` false and defers the decision. That makes
the feature miss its own motivating case, and the case is not hypothetical.
By its own header, `knapper-deploy.sh` has been revised four times in three
weeks: written 2026-08-16, §11b retention added 08-30, snapshots removed
09-02, §6b added 09-05. Every one of those was a replacement of a file already
in the vault. A script stored once is not a recurring workflow; a script under
continuous revision is, and it is the one this document cites.

So the fork is:

- **Enable conditional replacement in the first shipped version**, under the
  discipline the body already specifies — mandatory nonempty `expectSha256`,
  fresh re-read and hash check at commit under the lock, no upsert, no
  automatically substituted hash. `AllowReplace` stays a knob, and a
  deployment may still set it false; the default is what must be settled.
- **Or read brief §7 as forbidding conditional whole-file replacement too** —
  in which case this feature cannot serve the case that prompted it, and the
  document should say so rather than defer. Note the distinction the body
  already relies on: §7's prohibition is on UNCONDITIONAL writes, and
  `vault_edit` today legitimately replaces a whole file when its anchors span
  one, under exactly this precondition.

What must not ship is create-only presented as the answer to this problem. It
would deliver attachments and first-time script storage while leaving the
motivating case on the diff path it already had.

### C. The Cloudflare exception is gate ZERO, not the last gate

The body lists the edge exception under deployment gates, after
implementation. Reverse that.

Everything genuinely expensive here is the exception, not the code. A
path-scoped Bypass on the vault hostname is a deliberate hole in the property
the entire §6 ingress design exists to hold — that nothing reaches the vault
surface unauthenticated — on a host serving Family, Medical and Work notes.
The body's mitigations are correct, and none of them is the likely failure.
The likely failure is the edge behaving differently from its documentation:
path matching, policy precedence, adjacent paths, wrong Host, tunnel routing.
This deployment has already had one ingress verdict inverted by exactly that
class of surprise (2026-08-14, the followed login redirect that called an
exposed surface healthy).

So prove the exception on a disposable deployment BEFORE the grant store
exists: a route that demands a Knapper grant and refuses everything else;
adjacent and malformed paths still refused by Access; `/up` and the MCP root
unchanged; wrong Host and wrong method refused. If that cannot be
demonstrated, the rest is wasted work — and learning it costs a day instead of
the whole implementation.

### D. Two deploy consequences to record now

- **The locked tool surface goes 14 → 17 even with the feature disabled**,
  because the baseline keeps all three names in all three tables. That moves
  `knapper verify`'s tool-surface line and its schema and description-budget
  checks, the expected verify check count in `knapper-deploy.sh`, and the
  recorded counts in `Tech/Homelab/Proxmox/Knapper MCP.md` — together, in one
  change. Each new tool's description is also subject to the per-field 2048
  character client delivery budget (`ToolSchemaContract.ClientTextBudget`).
- **No existing agent session sees the new tools until it reconnects.**
  Attached clients cache tool manifests; measured on this deployment at
  v0.6.0, where a live session stayed at 13 tools after the server shipped 14.
  Enabling the feature and being able to test it from an agent are therefore
  two separate events, and the second one is not automatic.
