// Child-process probe for GENUINE two-process lock tests. The brief is
// explicit that the mutation lock must pass a real multi-process race test —
// an in-process test of flock proves nothing about the property that matters.
//
// Usage: Knapper.LockProbe <lockDir> path <relativePath> <holdMs> [timeoutMs]
//        Knapper.LockProbe <lockDir> commit -            <holdMs> [timeoutMs]
//
// Prints ACQUIRED (then holds, then RELEASED) or TIMEOUT. Exit: 0 held and
// released, 3 timeout, 2 usage.

using Knapper.Core;
using Knapper.Core.Locking;
using Knapper.Core.Vault;

if (args.Length is < 4 or > 5)
{
    Console.Error.WriteLine("usage: Knapper.LockProbe <lockDir> <path|commit> <relativePath|-> <holdMs> [timeoutMs]");
    return 2;
}

var lockDir = args[0];
var kind = args[1];
var relativePath = args[2];
var holdMs = int.Parse(args[3]);
var timeout = TimeSpan.FromMilliseconds(args.Length == 5 ? int.Parse(args[4]) : 5_000);

var manager = new VaultLockManager(lockDir);
try
{
    // The probe exercises the LOCK protocol, not path validation (which has
    // its own tests) — it builds the VaultPath record directly via the
    // internal constructor (InternalsVisibleTo).
    using var held = kind switch
    {
        "commit" => manager.AcquireCommitLock(timeout),
        "path" => manager.AcquirePathLock(
            new VaultPath { Relative = relativePath, Absolute = "/" + relativePath }, timeout),
        _ => throw new ArgumentException($"unknown kind '{kind}'"),
    };
    Console.WriteLine("ACQUIRED");
    Console.Out.Flush();
    Thread.Sleep(holdMs);
    Console.WriteLine("RELEASED");
    return 0;
}
catch (KnapperException e) when (e.Code == VaultErrorCode.LockTimeout)
{
    Console.WriteLine("TIMEOUT");
    return 3;
}
