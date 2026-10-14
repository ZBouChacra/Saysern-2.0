using System.Data;
using MySqlConnector;
using Sayserne.Api;
using Sayserne.Api.Data;
using Sayserne.Migrator;

namespace Sayserne.Tests;

public class DatabaseTests
{
    private static async Task<string> FixtureAsync(string name) =>
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    // ---- the application account can ONLY execute stored procedures ----

    [Theory]
    [InlineData("direct_select.sql")]
    [InlineData("direct_insert.sql")]
    [InlineData("direct_update.sql")]
    [InlineData("direct_drop.sql")]
    public async Task The_app_account_is_denied_direct_table_access(string fixture)
    {
        var db = await TestEnv.GetDatabaseAsync();
        await using var conn = new MySqlConnection(db.AppConnection);
        await conn.OpenAsync();
        var ex = await Assert.ThrowsAsync<MySqlException>(async () => await ScriptRunner.ExecuteAsync(conn, await FixtureAsync(fixture), default));
        Assert.True(ex.ErrorCode is MySqlErrorCode.TableAccessDenied or MySqlErrorCode.DatabaseAccessDenied or MySqlErrorCode.ColumnAccessDenied,
            $"unexpected error {ex.ErrorCode}: {ex.Message}");
    }

    [Fact]
    public async Task The_app_account_can_call_procedures_through_the_executor()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var users = new UserRepository(db.Executor());
        await users.PingAsync();
        Assert.Null(await users.GetByEmailAsync(ApiHost.UniqueEmail("absent")));
    }

    // ---- executor behaviour ----

    [Fact]
    public async Task Procedure_errors_surface_as_domain_errors()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var admin = new AdminUserRepository(db.Executor());
        var e1 = await Assert.ThrowsAsync<AppException>(() => admin.SetRoleAsync(1, "overlord"));
        Assert.Equal("INVALID_ROLE", e1.Code);
        var e2 = await Assert.ThrowsAsync<AppException>(() => admin.SetLockedAsync(long.MaxValue / 2, true));
        Assert.Equal("USER_NOT_FOUND", e2.Code);
        Assert.Equal(404, e2.Status);
    }

    [Fact]
    public async Task The_executor_refuses_non_procedure_names_before_touching_the_database()
    {
        var db = await TestEnv.GetDatabaseAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => db.Executor().ExecuteAsync("sp_x; DROP TABLE users", null));
    }

    [Fact]
    public async Task Hostile_text_in_parameters_is_data_not_sql()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var users = new UserRepository(db.Executor());
        var evil = "x'); DROP TABLE users; --@evil.test";
        var created = await users.CreateLocalAsync(evil, "hash", "Rob'; DROP TABLE users;--", "Tables");
        Assert.Equal(evil, created.Email);
        Assert.Equal("Rob'; DROP TABLE users;--", created.FirstName);
        Assert.NotNull(await users.GetByEmailAsync(evil));       // table still exists and the row is intact
    }

    [Fact]
    public async Task Duplicate_identity_and_missing_user_are_mapped_by_the_link_procedure()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var users = new UserRepository(db.Executor());
        var identities = new IdentityRepository(db.Executor());
        var u = await users.CreateLocalAsync(ApiHost.UniqueEmail("link"), "hash", "A", "B");
        var subject = "sub-" + Guid.NewGuid().ToString("N");
        await identities.LinkAsync(u.Id, "google", subject, u.Email, true);
        var again = await Assert.ThrowsAsync<AppException>(() => identities.LinkAsync(u.Id, "google", subject, u.Email, true));
        Assert.Equal("IDENTITY_ALREADY_LINKED", again.Code);
        var ghost = await Assert.ThrowsAsync<AppException>(() => identities.LinkAsync(long.MaxValue / 2, "google", "sub-" + Guid.NewGuid().ToString("N"), "a@b.co", true));
        Assert.Equal("USER_NOT_FOUND", ghost.Code);
    }

    [Fact]
    public async Task Linking_a_verified_identity_marks_the_matching_account_email_verified()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var users = new UserRepository(db.Executor());
        var u = await users.CreateLocalAsync(ApiHost.UniqueEmail("ver"), "hash", "A", "B");
        Assert.False(u.EmailVerified);
        var linked = await new IdentityRepository(db.Executor()).LinkAsync(u.Id, "google", "sub-" + Guid.NewGuid().ToString("N"), u.Email, true);
        Assert.True(linked.EmailVerified);
    }

    // ---- migrations ----

    [Fact]
    public async Task Migrations_apply_once_and_are_idempotent()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var again = await new MigrationRunner(db.MigratorConnection, TestEnv.DbDir).RunAsync();
        Assert.Equal(0, again.AppliedVersioned.Count);
        Assert.Equal(0, again.AppliedRepeatable.Count);
        Assert.True(again.Unchanged.Contains("V001__users_and_identities.sql"));
    }

    [Fact]
    public async Task A_fresh_database_gets_every_versioned_and_repeatable_script()
    {
        var fresh = await TestDatabase.CreateAsync(migrate: false);
        var result = await new MigrationRunner(fresh.MigratorConnection, TestEnv.DbDir).RunAsync();
        Assert.True(result.AppliedVersioned.Contains("V001__users_and_identities.sql"));
        Assert.True(result.AppliedRepeatable.Count >= 3);
        await new UserRepository(fresh.Executor()).PingAsync();   // procedures are live and callable by the restricted account
        await fresh.DropAsync();
    }

    [Fact]
    public async Task Editing_an_applied_versioned_migration_is_detected()
    {
        var work = CopyDbDir();
        var fresh = await TestDatabase.CreateAsync(migrate: false);
        try
        {
            await new MigrationRunner(fresh.MigratorConnection, work).RunAsync();
            var v1 = Path.Combine(work, "migrations", "V001__users_and_identities.sql");
            await File.AppendAllTextAsync(v1, "\n-- sneaky edit\n");
            var ex = await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(fresh.MigratorConnection, work).RunAsync());
            Assert.Contains("modified after it was applied", ex.Message);
        }
        finally { await fresh.DropAsync(); Directory.Delete(work, true); }
    }

    [Fact]
    public async Task A_changed_repeatable_script_is_redeployed_and_a_new_version_is_applied_in_order()
    {
        var work = CopyDbDir();
        var fresh = await TestDatabase.CreateAsync(migrate: false);
        try
        {
            await new MigrationRunner(fresh.MigratorConnection, work).RunAsync();
            await File.AppendAllTextAsync(Path.Combine(work, "migrations", "R__sp_users.sql"), "\n-- touched\n");
            await File.WriteAllTextAsync(Path.Combine(work, "migrations", "V002__add_phone.sql"), await FixtureAsync("V002__add_phone.sql"));
            var result = await new MigrationRunner(fresh.MigratorConnection, work).RunAsync();
            Assert.True(result.AppliedVersioned.Contains("V002__add_phone.sql"));
            Assert.True(result.AppliedRepeatable.Contains("R__sp_users.sql"));
            Assert.Equal(1, result.AppliedRepeatable.Count);
        }
        finally { await fresh.DropAsync(); Directory.Delete(work, true); }
    }

    [Fact]
    public async Task Badly_named_migration_files_are_rejected()
    {
        var work = CopyDbDir();
        var fresh = await TestDatabase.CreateAsync(migrate: false);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "migrations", "add_something.sql"), "SELECT 1;");
            await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(fresh.MigratorConnection, work).RunAsync());
        }
        finally { await fresh.DropAsync(); Directory.Delete(work, true); }
    }

    // ---- last-admin protection (needs an isolated database: other tests create admins in the shared one) ----

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_or_locked()
    {
        var fresh = await TestDatabase.CreateAsync();
        try
        {
            var users = new UserRepository(fresh.Executor());
            var admin = new AdminUserRepository(fresh.Executor());
            var a = await users.CreateLocalAsync("a@example.test", "hash", "A", "A");
            var b = await users.CreateLocalAsync("b@example.test", "hash", "B", "B");
            await fresh.PromoteToAdminAsync(a.Email);

            Assert.Equal("LAST_ADMIN", (await Assert.ThrowsAsync<AppException>(() => admin.SetRoleAsync(a.Id, "user"))).Code);
            Assert.Equal("LAST_ADMIN", (await Assert.ThrowsAsync<AppException>(() => admin.SetLockedAsync(a.Id, true))).Code);

            await admin.SetRoleAsync(b.Id, "admin");                 // a second admin exists now
            var demoted = await admin.SetRoleAsync(a.Id, "user");    // so demoting the first one is fine
            Assert.Equal("user", demoted.Role);
            Assert.Equal("LAST_ADMIN", (await Assert.ThrowsAsync<AppException>(() => admin.SetRoleAsync(b.Id, "user"))).Code);
        }
        finally { await fresh.DropAsync(); }
    }

    [Fact]
    public async Task Promote_admin_cli_procedure_reports_unknown_users()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var ex = await Assert.ThrowsAsync<MySqlException>(() => db.PromoteToAdminAsync(ApiHost.UniqueEmail("ghost")));
        Assert.Equal("USER_NOT_FOUND", ex.Message);
    }

    private static string CopyDbDir()
    {
        var target = Path.Combine(Path.GetTempPath(), "sayserne-db-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(TestEnv.DbDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(TestEnv.DbDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return target;
    }
}
