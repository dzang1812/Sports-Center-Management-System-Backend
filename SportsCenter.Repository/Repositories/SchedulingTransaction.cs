using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SportsCenter.Repository.Data;

namespace SportsCenter.Repository.Repositories;

public enum ScheduleResourceType : long
{
    Sport = 1, Member = 2, Coach = 3, Room = 4, Class = 5, Session = 6, PtBooking = 7, MembershipPlan = 8,
}

public readonly record struct ScheduleResourceLock(ScheduleResourceType Type, int Id)
{
    public long Key => (long)Type * 10_000_000_000L + Id;
}

// Thrown when a concurrent writer wins (serialization failure, deadlock, unique violation, stale row).
// The API maps it to 409 SCHEDULE_CONFLICT.
public sealed class ScheduleConflictException() : Exception("Dữ liệu lịch vừa thay đổi. Vui lòng tải lại và thử lại.");

// Unit of work for scheduling: one transaction, advisory locks per resource, SaveChanges.
public interface ISchedulingTransaction
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
    Task LockAsync(IEnumerable<ScheduleResourceLock> locks, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed class SchedulingTransaction(AppDbContext db) : ISchedulingTransaction
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        // READ COMMITTED deliberately pairs with transaction-scoped advisory locks: statements
        // waiting on a resource lock must see the commit that released it, not an older snapshot.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var result = await work(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (IsConcurrency(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ScheduleConflictException();
        }
    }

    public async Task LockAsync(IEnumerable<ScheduleResourceLock> locks, CancellationToken cancellationToken = default)
    {
        foreach (var key in locks.Select(x => x.Key).Distinct().Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);

    private static bool IsConcurrency(Exception ex) => ex is DbUpdateConcurrencyException ||
        ex is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation } ||
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation };
}
