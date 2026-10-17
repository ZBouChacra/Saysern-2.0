using MySqlConnector;

namespace Sayserne.Api.Data;

/// <summary>
/// MySQL may abort one of several transactions that touch the same rows at the same moment (error 1213, "deadlock"). Nothing was changed, so the right
/// response is a short wait and another try. Any other error is real and is not retried.
/// </summary>
public static class DeadlockRetry
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, int attempts = 6)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await operation(); }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < attempts) { await Task.Delay(Random.Shared.Next(10, 40) * attempt); }
        }
    }

    public static Task RunAsync(Func<Task> operation, int attempts = 6) => RunAsync(async () => { await operation(); return 0; }, attempts);
}
