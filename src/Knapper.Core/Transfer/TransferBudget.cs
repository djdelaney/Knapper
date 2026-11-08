using Knapper.Core.Options;

namespace Knapper.Core.Transfer;

/// <summary>
/// The ONE byte budget and record cap both transfer directions draw from
/// (docs/proposals/transfer-grants.md §10): <c>Transfer:MaxBufferedBytes</c>
/// over download snapshots AND upload buffers, and
/// <c>Transfer:MaxTrackedGrants</c> over every grant record, active or
/// retained-terminal. Two separate budgets would each admit their own maximum
/// and together exceed the memory the setting promises.
///
/// Atomic take-or-refuse: a request either gets all it asked for or nothing,
/// so a refusal never leaves a partial reservation behind. Stores call it
/// while holding their own lock; it never calls back, so the lock order is
/// always store → budget.
/// </summary>
public sealed class TransferBudget(TransferOptions transfer)
{
    private readonly Lock _lock = new();
    private long _bytes;
    private int _records;

    /// <summary>
    /// Take <paramref name="bytes"/> bytes and <paramref name="records"/>
    /// record slots, or nothing: null on success, otherwise why not (for the
    /// caller's typed capacity error).
    /// </summary>
    public string? TryTake(long bytes, int records)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(records);
        lock (_lock)
        {
            if (_records + records > transfer.MaxTrackedGrants)
                return $"{_records} grant records are tracked (Transfer:MaxTrackedGrants {transfer.MaxTrackedGrants})";
            long after;
            try
            {
                after = checked(_bytes + bytes);
            }
            catch (OverflowException)
            {
                return "the transfer byte budget would overflow";
            }
            if (after > transfer.MaxBufferedBytes)
                return $"the transfer byte budget is full ({_bytes} of {transfer.MaxBufferedBytes} bytes in use)";
            _bytes = after;
            _records += records;
            return null;
        }
    }

    /// <summary>Give back what an earlier <see cref="TryTake"/> took (in any split across calls).</summary>
    public void Return(long bytes, int records)
    {
        lock (_lock)
        {
            _bytes -= bytes;
            _records -= records;
        }
    }

    public long Bytes
    {
        get
        {
            lock (_lock)
                return _bytes;
        }
    }

    public int Records
    {
        get
        {
            lock (_lock)
                return _records;
        }
    }
}
