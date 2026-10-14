using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace Sayserne.Api.Data;

/// <summary>Named, typed arguments for a procedure call.</summary>
public sealed class ProcParams
{
    private readonly List<(string Name, object? Value)> _items = [];
    public IReadOnlyList<(string Name, object? Value)> Items => _items;
    public ProcParams Add(string name, object? value) { _items.Add((name, value)); return this; }
}

/// <summary>
/// The ONLY gateway to the database. Every call is a stored-procedure invocation
/// (<see cref="CommandType.StoredProcedure"/>) with parameters; there is no way to pass SQL text.
/// </summary>
public interface IStoredProcedureExecutor
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string procedure, ProcParams? args, Func<DbDataReader, T> map, CancellationToken ct = default);
    Task<T?> QuerySingleOrDefaultAsync<T>(string procedure, ProcParams? args, Func<DbDataReader, T> map, CancellationToken ct = default) where T : class;
    Task<(IReadOnlyList<T1> First, IReadOnlyList<T2> Second)> QueryTwoAsync<T1, T2>(
        string procedure, ProcParams? args, Func<DbDataReader, T1> map1, Func<DbDataReader, T2> map2, CancellationToken ct = default);
    Task ExecuteAsync(string procedure, ProcParams? args, CancellationToken ct = default);
}

public sealed partial class StoredProcedureExecutor(MySqlDataSource dataSource) : IStoredProcedureExecutor
{
    [GeneratedRegex("^sp_[a-z0-9_]{1,60}$")]
    private static partial Regex ProcedureNamePattern();

    /// <summary>Only well-formed procedure identifiers are accepted, so a name can never smuggle SQL.</summary>
    public static void ValidateProcedureName(string name)
    {
        if (!ProcedureNamePattern().IsMatch(name ?? string.Empty))
            throw new ArgumentException($"'{name}' is not a valid stored procedure name (expected sp_<lowercase_name>).", nameof(name));
    }

    public Task<IReadOnlyList<T>> QueryAsync<T>(string procedure, ProcParams? args, Func<DbDataReader, T> map, CancellationToken ct = default) =>
        RunAsync(procedure, args, async reader => (IReadOnlyList<T>)await ReadAllAsync(reader, map, ct), ct);

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string procedure, ProcParams? args, Func<DbDataReader, T> map, CancellationToken ct = default) where T : class
    {
        var rows = await QueryAsync(procedure, args, map, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<(IReadOnlyList<T1> First, IReadOnlyList<T2> Second)> QueryTwoAsync<T1, T2>(
        string procedure, ProcParams? args, Func<DbDataReader, T1> map1, Func<DbDataReader, T2> map2, CancellationToken ct = default) =>
        RunAsync(procedure, args, async reader =>
        {
            var first = await ReadAllAsync(reader, map1, ct);
            var second = await reader.NextResultAsync(ct) ? await ReadAllAsync(reader, map2, ct) : [];
            return ((IReadOnlyList<T1>)first, (IReadOnlyList<T2>)second);
        }, ct);

    public Task ExecuteAsync(string procedure, ProcParams? args, CancellationToken ct = default) =>
        RunAsync(procedure, args, reader => Task.FromResult(0), ct);

    private static async Task<List<T>> ReadAllAsync<T>(DbDataReader reader, Func<DbDataReader, T> map, CancellationToken ct)
    {
        var list = new List<T>();
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }

    private async Task<TResult> RunAsync<TResult>(string procedure, ProcParams? args, Func<DbDataReader, Task<TResult>> consume, CancellationToken ct)
    {
        ValidateProcedureName(procedure);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var command = new MySqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
            if (args != null)
                foreach (var (name, value) in args.Items)
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await consume(reader);
        }
        catch (MySqlException ex) when (ex.SqlState == "45000")
        {
            // SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '<CODE>' in a procedure => domain error.
            throw new AppException(ex.Message);
        }
    }
}
