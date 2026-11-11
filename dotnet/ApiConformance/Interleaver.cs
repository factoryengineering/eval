using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ApiConformance;

public sealed partial class Interleaver
{
    public const string ParticipantHeader = "X-Test-Participant";

    private static readonly AsyncLocal<string?> Current = new();

    private enum Phase { Running, Waiting, Done }

    private sealed class Participant(string id)
    {
        public string Id { get; } = id;
        public Phase Phase { get; set; } = Phase.Running;
        public bool PendingWrite { get; set; }
        public string PendingSql { get; set; } = "";
        public TaskCompletionSource Release { get; set; } = NewSignal();
        public TaskCompletionSource Parked { get; } = NewSignal();
    }

    private readonly object sync = new();
    private readonly List<string> events = [];
    private List<Participant> participants = [];
    private string? writer;
    private bool aborted;

    public IReadOnlyList<string> Events
    {
        get { lock (sync) return [.. events]; }
    }

    public IDbCommandInterceptor CommandInterceptor => new GateCommandInterceptor(this);
    public IDbTransactionInterceptor TransactionInterceptor => new GateTransactionInterceptor(this);

    public void Arm(IEnumerable<string> ids)
    {
        lock (sync)
        {
            participants = [.. ids.Select(id => new Participant(id))];
            writer = null;
            aborted = false;
            events.Clear();
        }
    }

    public void Abort()
    {
        lock (sync)
        {
            aborted = true;
            foreach (var participant in participants) participant.Release.TrySetResult();
        }
    }

    public string Describe()
    {
        lock (sync)
        {
            var state = string.Join(", ", participants.Select(p => $"{p.Id}={p.Phase}{(p.Phase == Phase.Waiting ? $" before [{p.PendingSql}]" : "")}"));
            return $"Interleaver state: {state}; write lock held by {writer ?? "nobody"}." + Environment.NewLine + string.Join(Environment.NewLine, events.Select(e => "  " + e));
        }
    }

    public async Task<T[]> Run<T>(IReadOnlyList<Func<string, Task<T>>> sends, TimeSpan timeout)
    {
        var ids = sends.Select((_, index) => index.ToString()).ToList();
        Arm(ids);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var started = new List<Task<T>>();
        try
        {
            for (var index = 0; index < sends.Count; index++)
            {
                var send = sends[index];
                var id = ids[index];
                started.Add(Task.Run(() => send(id)));
                if (index < sends.Count - 1) await ParkedTask(id).WaitAsync(Remaining(timeout, clock));
            }
            return await Task.WhenAll(started).WaitAsync(Remaining(timeout, clock));
        }
        catch (TimeoutException)
        {
            var description = Describe();
            Abort();
            throw new ScenarioFailure($"The requests did not finish within {timeout.TotalSeconds:0}s under lockstep (hang or deadlock). {description}");
        }
        finally
        {
            Disarm();
        }
    }

    private static TimeSpan Remaining(TimeSpan timeout, System.Diagnostics.Stopwatch clock) => timeout > clock.Elapsed ? timeout - clock.Elapsed : TimeSpan.Zero;

    private Task ParkedTask(string id)
    {
        lock (sync) return participants.First(p => p.Id == id).Parked.Task;
    }

    private void Disarm()
    {
        lock (sync)
        {
            aborted = true;
            participants = [];
            writer = null;
        }
    }

    public RequestDelegate Middleware(RequestDelegate next) => async context =>
    {
        var id = context.Request.Headers[ParticipantHeader].ToString();
        if (id.Length == 0)
        {
            await next(context);
            return;
        }
        Current.Value = id;
        try
        {
            await next(context);
        }
        finally
        {
            Current.Value = null;
            Finish(id);
        }
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task Before(string sql, bool write = false)
    {
        if (Current.Value is not { } id) return Task.CompletedTask;
        lock (sync)
        {
            var participant = participants.FirstOrDefault(p => p.Id == id);
            if (participant is null || aborted) return Task.CompletedTask;
            participant.Phase = Phase.Waiting;
            participant.Parked.TrySetResult();
            participant.PendingSql = Summary(sql);
            participant.PendingWrite = write || WriteCommand().IsMatch(sql);
            participant.Release = NewSignal();
            events.Add($"{id} arrives before [{participant.PendingSql}]");
            Advance();
            return participant.Release.Task;
        }
    }

    private void Finish(string id)
    {
        lock (sync)
        {
            var participant = participants.FirstOrDefault(p => p.Id == id);
            if (participant is null) return;
            participant.Phase = Phase.Done;
            participant.Parked.TrySetResult();
            if (writer == id) writer = null;
            events.Add($"{id} done");
            Advance();
        }
    }

    private void ReleaseWriteLock(bool whenTransactionEnds)
    {
        if (Current.Value is not { } id) return;
        lock (sync)
        {
            if (writer != id) return;
            writer = null;
            if (whenTransactionEnds) events.Add($"{id} write transaction ended");
            Advance();
        }
    }

    private void Advance()
    {
        if (aborted || participants.Any(p => p.Phase == Phase.Running)) return;
        var released = new List<Participant>();
        foreach (var participant in participants.Where(p => p.Phase == Phase.Waiting))
        {
            if (participant.PendingWrite && writer is not null && writer != participant.Id)
            {
                events.Add($"{participant.Id} blocked by the write lock of {writer} before [{participant.PendingSql}]");
                continue;
            }
            if (participant.PendingWrite) writer = participant.Id;
            released.Add(participant);
        }
        foreach (var participant in released)
        {
            participant.Phase = Phase.Running;
            events.Add($"{participant.Id} runs [{participant.PendingSql}]");
        }
        foreach (var participant in released) participant.Release.TrySetResult();
    }

    private static string Summary(string sql)
    {
        var line = Whitespace().Replace(sql.Trim(), " ");
        return line.Length > 60 ? line[..60] : line;
    }

    [GeneratedRegex(@"^\s*(INSERT|UPDATE|DELETE|REPLACE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WriteCommand();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class GateCommandInterceptor(Interleaver gate) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            gate.Before(command.CommandText).GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await gate.Before(command.CommandText);
            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            gate.Before(command.CommandText).GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await gate.Before(command.CommandText);
            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            gate.Before(command.CommandText).GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await gate.Before(command.CommandText);
            return result;
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            AfterStatement(command);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            AfterStatement(command);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            AfterStatement(command);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            AfterStatement(command);
            return ValueTask.FromResult(result);
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => AfterStatement(command);

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            AfterStatement(command);
            return Task.CompletedTask;
        }

        private void AfterStatement(DbCommand command)
        {
            if (command.Transaction is null) gate.ReleaseWriteLock(false);
        }
    }

    private sealed class GateTransactionInterceptor(Interleaver gate) : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            gate.Before("BEGIN TRANSACTION", write: true).GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            await gate.Before("BEGIN TRANSACTION", write: true);
            return result;
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => gate.ReleaseWriteLock(true);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            gate.ReleaseWriteLock(true);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => gate.ReleaseWriteLock(true);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            gate.ReleaseWriteLock(true);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => gate.ReleaseWriteLock(true);

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            gate.ReleaseWriteLock(true);
            return Task.CompletedTask;
        }
    }
}
