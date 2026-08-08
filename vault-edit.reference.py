#!/usr/bin/env python3
"""
vault-edit.py — locally concurrency-safe reads and anchored edits for this vault.

This vault has concurrent writers. On one machine, this tool serializes
cooperating writers with an advisory lock, checks a fresh sha256
precondition, applies exact-count anchors and guard strings, commits via an
atomic same-directory operation, and verifies by reopening the file.

The lock is local to this machine. It cannot coordinate two unsynchronized
Obsidian Sync replicas or software that writes the vault without using this
tool. The hash precondition detects remote changes only after they have
arrived on this machine; it is not a distributed compare-and-swap.

Usage:
  python3 vault-edit.py read   <file> [--print]
  python3 vault-edit.py edit   <file>     # JSON spec on stdin
  python3 vault-edit.py append <file>     # JSON on stdin: {"expect_sha256": "...", "text": "..."}
  python3 vault-edit.py create <file>     # JSON on stdin: {"text": "..."}

'edit' stdin spec:
  {
    "expect_sha256": "<sha256 from a 'read' moments earlier — REQUIRED>",
    "edits":  [ {"old": "<anchor>", "new": "<replacement>", "count": 1}, ... ],
    "guards": [ "<string that must be present before AND survive after>", ... ]
  }

Rules enforced:
  - edit/append hold a local per-path advisory lock from the fresh read
    through verification. All local automated writers must use this tool.
  - expect_sha256 must match the bytes on disk under that lock. If not, exit 3:
    someone else wrote the file since your read. Re-read and rebuild the
    edit against the current content. NEVER retry with the old base.
  - Each "old" must occur exactly "count" times (default 1) in the text as
    it stands when that edit is applied (edits apply sequentially). Exit 5
    on mismatch, file untouched.
  - Guards must be present before the edit and after the write.
  - Refuses paths outside this vault, symlink file arguments, and anything
    under .obsidian/ or .git/.
  - Writes go to a hidden temp file in the same directory, then rename —
    Obsidian Sync never sees a half-written note.
  - create uses an atomic no-clobber commit and cannot replace a file that
    appears concurrently.
  - After writing, the file is reopened and verified: bytes match exactly
    and every guard survived.

Output: one JSON object on stdout. On success it includes new_sha256, but
CLAUDE.md still requires a fresh read immediately before each later write.

Exit codes:
  0 ok · 2 usage/IO/encoding · 3 precondition/conflict failed ·
  4 guard missing · 5 anchor count mismatch · 6 post-write verify failed

Typical agent invocation (heredoc avoids all shell-quoting pain):
  python3 vault-edit.py edit "Tech/Some Note.md" <<'JSON'
  {"expect_sha256": "abc...",
   "edits": [{"old": "old line", "new": "new line"}],
   "guards": ["## Section that must survive"]}
  JSON
"""
import argparse
import fcntl
import hashlib
import json
import os
import stat
import sys
import tempfile
from contextlib import contextmanager
from pathlib import Path

BANNED_DIRS = {".obsidian", ".git"}
VAULT_ROOT = Path(__file__).resolve().parent


def out(obj, code=0):
    print(json.dumps(obj, ensure_ascii=False))
    sys.exit(code)


def fail(code, error, **extra):
    obj = {"ok": False, "error": error}
    obj.update(extra)
    out(obj, code)


def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


def load_stdin_json():
    raw = sys.stdin.read()
    try:
        spec = json.loads(raw)
    except json.JSONDecodeError as e:
        fail(2, "stdin is not valid JSON: %s" % e)
    if not isinstance(spec, dict):
        fail(2, "stdin JSON must be an object")
    return spec


def checked_path(arg):
    p = Path(arg)
    if not p.is_absolute():
        p = Path.cwd() / p
    try:
        rp = p.resolve(strict=False)
    except OSError as e:
        fail(2, "cannot resolve path: %s" % e)
    try:
        relative = rp.relative_to(VAULT_ROOT)
    except ValueError:
        fail(2, "path is outside this vault: %s" % rp)
    if p.is_symlink():
        fail(2, "refusing symlink file argument: %s" % p)
    for banned in BANNED_DIRS:
        if banned in relative.parts:
            fail(2, "refusing to touch anything under %s/" % banned)
    return rp


def read_file_bytes(p):
    try:
        return p.read_bytes()
    except FileNotFoundError:
        fail(2, "no such file: %s" % p)
    except OSError as e:
        fail(2, "read failed: %s" % e)


def decode_utf8(data):
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        fail(2, "file is not valid UTF-8 text; this tool only edits text files")


def fsync_dir(d):
    try:
        dfd = os.open(str(d), os.O_RDONLY)
        try:
            os.fsync(dfd)
        finally:
            os.close(dfd)
    except OSError:
        pass  # some mounted filesystems refuse dir fsync; non-fatal


@contextmanager
def exclusive_lock(p):
    """Serialize cooperating writers to one resolved path on this machine."""
    lock_dir = (Path(tempfile.gettempdir()) /
                ("vault-edit-locks-%s" % os.getuid()))
    try:
        lock_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
        if lock_dir.is_symlink():
            fail(2, "lock directory must not be a symlink: %s" % lock_dir)
        os.chmod(lock_dir, 0o700)
        lock_name = sha256_hex(str(p).encode("utf-8")) + ".lock"
        fd = os.open(str(lock_dir / lock_name), os.O_CREAT | os.O_RDWR, 0o600)
    except OSError as e:
        fail(2, "cannot open local edit lock: %s" % e)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(fd, fcntl.LOCK_UN)
    finally:
        os.close(fd)


def write_temp(d, data, mode):
    """Create and fsync a complete hidden temp file, returning its path."""
    fd, tmp = tempfile.mkstemp(dir=str(d), prefix=".vault-edit-tmp-")
    try:
        with os.fdopen(fd, "wb") as f:
            os.fchmod(f.fileno(), mode)
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        return tmp
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def atomic_write(p, data, expected_sha256):
    """Replace an existing file atomically after a final stale-base check."""
    d = p.parent
    mode = stat.S_IMODE(p.stat().st_mode)
    tmp = write_temp(d, data, mode)
    try:
        latest = read_file_bytes(p)
        latest_sha256 = sha256_hex(latest)
        if latest_sha256 != expected_sha256:
            fail(3, "precondition failed while edit was being prepared — "
                    "re-read and rebuild the edit against current content",
                 expected_sha256=expected_sha256,
                 current_sha256=latest_sha256)
        os.replace(tmp, str(p))
        tmp = None
    finally:
        if tmp is not None:
            try:
                os.unlink(tmp)
            except OSError:
                pass
    fsync_dir(d)


def atomic_create(p, data):
    """Create p atomically without replacing a path that appears concurrently."""
    d = p.parent
    tmp = write_temp(d, data, 0o644)
    try:
        try:
            os.link(tmp, str(p))
        except FileExistsError:
            fail(3, "creation precondition failed: file already exists: %s" % p)
    finally:
        try:
            os.unlink(tmp)
        except OSError:
            pass
    fsync_dir(d)


def check_precondition(spec, data):
    exp = spec.get("expect_sha256")
    if not exp or not isinstance(exp, str):
        fail(2, "expect_sha256 is required — run 'read' first and pass its sha256")
    exp = exp.strip().lower()
    cur = sha256_hex(data)
    if cur != exp:
        fail(3, "precondition failed: file changed since your read — "
                "re-read and rebuild the edit against current content",
             expected_sha256=exp, current_sha256=cur)
    return cur


def verify_after_write(p, written, guards=()):
    data2 = read_file_bytes(p)
    problems = []
    if data2 != written:
        problems.append("bytes on disk differ from what was written (concurrent write landed?)")
    if guards:
        text2 = data2.decode("utf-8", errors="replace")
        for g in guards:
            if g not in text2:
                problems.append("guard missing after write: %r" % g[:80])
    if problems:
        fail(6, "post-write verification failed", problems=problems,
             sha256_on_disk=sha256_hex(data2))
    return data2


def cmd_read(args):
    p = checked_path(args.file)
    data = read_file_bytes(p)
    result = {
        "ok": True,
        "path": str(p),
        "sha256": sha256_hex(data),
        "bytes": len(data),
        "lines": len(data.splitlines()),
    }
    if args.print_content:
        result["content"] = decode_utf8(data)
    out(result)


def cmd_edit(args):
    p = checked_path(args.file)
    spec = load_stdin_json()
    edits = spec.get("edits")
    if not isinstance(edits, list) or not edits:
        fail(2, "edits[] is required and must be non-empty")
    guards = spec.get("guards") or []
    if not isinstance(guards, list):
        fail(2, "guards must be a list of strings")

    with exclusive_lock(p):
        data = read_file_bytes(p)
        cur = check_precondition(spec, data)
        text = decode_utf8(data)

        for g in guards:
            if not isinstance(g, str) or not g:
                fail(2, "each guard must be a non-empty string")
            if g not in text:
                fail(4, "guard not present before edit — wrong file or stale assumptions",
                     guard=g[:120])

        new_text = text
        for i, e in enumerate(edits):
            if not isinstance(e, dict):
                fail(2, "edit[%d] must be an object" % i)
            old, new = e.get("old"), e.get("new")
            if not isinstance(old, str) or not old:
                fail(2, "edit[%d]: 'old' must be a non-empty string" % i)
            if not isinstance(new, str):
                fail(2, "edit[%d]: 'new' must be a string" % i)
            if old == new:
                fail(2, "edit[%d]: old == new" % i)
            want = e.get("count", 1)
            if isinstance(want, bool) or not isinstance(want, int):
                fail(2, "edit[%d]: count must be an integer" % i)
            if want < 1:
                fail(2, "edit[%d]: count must be >= 1" % i)
            n = new_text.count(old)
            if n != want:
                fail(5, "edit[%d]: anchor matched %d times, expected exactly %d — "
                        "file untouched" % (i, n, want), anchor=old[:120])
            new_text = new_text.replace(old, new)

        if new_text == text:
            fail(2, "edits produced no change")
        for g in guards:
            if g not in new_text:
                fail(4, "guard would not survive edit — file untouched", guard=g[:120])

        written = new_text.encode("utf-8")
        atomic_write(p, written, expected_sha256=cur)
        data2 = verify_after_write(p, written, guards)

    out({"ok": True, "path": str(p), "edits_applied": len(edits),
         "old_sha256": cur, "new_sha256": sha256_hex(data2),
         "bytes_before": len(data), "bytes_after": len(data2), "verified": True})


def cmd_append(args):
    p = checked_path(args.file)
    spec = load_stdin_json()
    text_add = spec.get("text")
    if not isinstance(text_add, str) or not text_add:
        fail(2, "text is required and must be a non-empty string "
                "(include leading newline yourself if needed)")
    with exclusive_lock(p):
        data = read_file_bytes(p)
        cur = check_precondition(spec, data)
        decode_utf8(data)  # ensure text file
        written = data + text_add.encode("utf-8")
        atomic_write(p, written, expected_sha256=cur)
        data2 = verify_after_write(p, written)
    out({"ok": True, "path": str(p), "old_sha256": cur,
         "new_sha256": sha256_hex(data2), "bytes_before": len(data),
         "bytes_after": len(data2), "verified": True})


def cmd_create(args):
    p = checked_path(args.file)
    spec = load_stdin_json()
    text = spec.get("text")
    if not isinstance(text, str):
        fail(2, "text is required (may be empty string)")
    with exclusive_lock(p):
        if not p.parent.is_dir():
            fail(2, "parent directory does not exist: %s (folder creation is a "
                    "deliberate act — do it explicitly first)" % p.parent)
        written = text.encode("utf-8")
        atomic_create(p, written)
        data2 = verify_after_write(p, written)
    out({"ok": True, "path": str(p), "created": True,
         "sha256": sha256_hex(data2), "bytes": len(data2), "verified": True})


def main():
    ap = argparse.ArgumentParser(
        description="Locally concurrency-safe anchored edits for the vault. "
                    "See module docstring for the JSON stdin formats.")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p_read = sub.add_parser("read", help="print sha256/size; --print adds content")
    p_read.add_argument("file")
    p_read.add_argument("--print", dest="print_content", action="store_true")
    p_read.set_defaults(func=cmd_read)

    p_edit = sub.add_parser(
        "edit", help="locked, hash-checked anchored edit; JSON spec on stdin")
    p_edit.add_argument("file")
    p_edit.set_defaults(func=cmd_edit)

    p_app = sub.add_parser(
        "append", help="locked, hash-checked append; JSON on stdin")
    p_app.add_argument("file")
    p_app.set_defaults(func=cmd_append)

    p_new = sub.add_parser("create", help="create new file (fails if exists); JSON on stdin")
    p_new.add_argument("file")
    p_new.set_defaults(func=cmd_create)

    args = ap.parse_args()
    try:
        args.func(args)
    except OSError as e:
        fail(2, "filesystem operation failed: %s" % e)


if __name__ == "__main__":
    main()
