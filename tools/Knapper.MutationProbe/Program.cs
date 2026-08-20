// Child-process probe for GENUINE two-process mutation races (brief §7/§13:
// stale-edit and simultaneous-create tests must run across real processes).
// Executes ONE mutation through the real VaultMutationService and prints a
// single JSON result line.
//
// Usage:
//   Knapper.MutationProbe edit   <vaultRoot> <lockDir> <path> <expectSha> <old> <new>
//   Knapper.MutationProbe create <vaultRoot> <lockDir> <path> <text>
//   Knapper.MutationProbe append <vaultRoot> <lockDir> <path> <expectSha> <text>
//
// And the crash modes, which exist because a durable disappearance can only
// be observed from OUTSIDE the process that caused it — a try/finally proves
// nothing about a machine that lost power mid-operation:
//
//   Knapper.MutationProbe crash-move   <vaultRoot> <lockDir> <path> <destination> <expectSha> <killPoint>
//   Knapper.MutationProbe crash-delete <vaultRoot> <lockDir> <path> <expectSha> <killPoint>
//
// killPoint = after-link | after-commit | after-capture. The process is
// terminated with Environment.FailFast, so no finally, no cleanup, no
// rollback — exactly what a kill -9 or a power cut leaves behind.
//
// Exit 0 = mutation applied; 1 = typed rejection (JSON carries the code);
// anything else = the deliberate crash.

using System.Text.Json;
using Knapper.Core;
using Knapper.Core.Generation;
using Knapper.Core.Locking;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Vault;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: Knapper.MutationProbe <edit|create|append> <vaultRoot> <lockDir> <path> ...");
    return 2;
}

var command = args[0];
var vaultRoot = args[1];
var lockDir = args[2];
var path = args[3];

var resolver = new VaultPathResolver(vaultRoot);
var options = new VaultOptions { RootPath = vaultRoot, LockDirectory = lockDir };
var service = new VaultMutationService(
    resolver,
    new VaultLockManager(lockDir),
    new VaultGenerationCounter(),
    new ConflictDetector(resolver),
    StaticSyncGate.Open,
    options,
    new SyncOptions());

if (command is "crash-move" or "crash-delete")
{
    var killPoint = args[^1];
    void Die(string at)
    {
        if (at == killPoint)
            Environment.FailFast($"deliberate crash at {at}");
    }
    service.AfterLinkTestHook = _ => Die("after-link");
    service.AfterCommitTestHook = (_, _) => Die("after-commit");
    service.AfterCaptureTestHook = (_, _) => Die("after-capture");

    if (command == "crash-move")
        service.Move(path, args[4], args[5]);
    else
        service.Delete(path, args[4]);
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, code = (string?)null, newSha = (string?)null }));
    return 0;
}

try
{
    var result = command switch
    {
        "edit" => service.Edit(path, args[4], [new EditSpec(args[5], args[6])]),
        "create" => service.Create(path, args[4]),
        "append" => service.Append(path, args[4], args[5]),
        _ => throw new ArgumentException($"unknown command '{command}'"),
    };
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, code = (string?)null, newSha = result.NewSha256 }));
    return 0;
}
catch (KnapperException e)
{
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, code = e.Code.ToString(), newSha = (string?)null }));
    return 1;
}
