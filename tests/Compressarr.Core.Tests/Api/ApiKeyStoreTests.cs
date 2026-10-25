using Compressarr.Core.Api;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Tests.Presets;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Tests.Api;

/// <summary>The Automation API's two fixed keys, as Sonarr keeps its one: an API Key (control) and a Read-only API Key, made on first use, always readable, regenerated at will.</summary>
public class ApiKeyStoreTests : AppDataTestBase
{
    private static SqliteApiKeyStore NewStore() => new(new SqliteResumeStateStore());

    [Fact]
    public void BothKeysExist_FromTheFirstAsk_LookLikeKeys_AndDiffer()
    {
        var store = NewStore();

        var control = store.GetKey(ApiKeyScope.Control);
        var read = store.GetKey(ApiKeyScope.Read);

        Assert.StartsWith("cmpk_", control);
        Assert.Equal(48, control.Length);   // cmpk_ and 43 characters of 256 random bits
        Assert.Matches("^[A-Za-z0-9_-]+$", control);
        Assert.NotEqual(control, read);
    }

    [Fact]
    public void TheKeysAreTheSameEveryTime_AndSurviveARestart()
    {
        var control = NewStore().GetKey(ApiKeyScope.Control);

        Assert.Equal(control, NewStore().GetKey(ApiKeyScope.Control));
        Assert.Equal(control, NewStore().GetKey(ApiKeyScope.Control));
    }

    [Fact]
    public void EachKey_OpensTheDoor_WithItsOwnScopeAndName()
    {
        var store = NewStore();

        var control = store.Authenticate(store.GetKey(ApiKeyScope.Control));
        var read = store.Authenticate(store.GetKey(ApiKeyScope.Read));

        Assert.Equal(new ApiKeyInfo("API Key", ApiKeyScope.Control), control);
        Assert.Equal(new ApiKeyInfo("Read-only API Key", ApiKeyScope.Read), read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cmpk_notakey")]
    [InlineData("cmpk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void AWrongKey_IsNotAccepted(string key)
    {
        var store = NewStore();
        store.GetKey(ApiKeyScope.Control);

        Assert.Null(store.Authenticate(key));
    }

    [Fact]
    public void AKeyOfAnotherCase_IsNotTheSameKey()
    {
        var store = NewStore();

        Assert.Null(store.Authenticate(store.GetKey(ApiKeyScope.Control).ToUpperInvariant()));
    }

    [Fact]
    public void Regenerating_ReplacesOneKey_TheOldOneStopsAtOnce_AndTheOtherIsUntouched()
    {
        var store = NewStore();
        var oldControl = store.GetKey(ApiKeyScope.Control);
        var read = store.GetKey(ApiKeyScope.Read);

        var newControl = store.Regenerate(ApiKeyScope.Control);

        Assert.NotEqual(oldControl, newControl);
        Assert.Equal(newControl, store.GetKey(ApiKeyScope.Control));
        Assert.Null(store.Authenticate(oldControl));
        Assert.NotNull(store.Authenticate(newControl));
        Assert.Equal(read, store.GetKey(ApiKeyScope.Read));
        Assert.NotNull(store.Authenticate(read));
    }

    [Fact]
    public void RegeneratingBeforeTheKeysWereEverShown_Works()
    {
        var store = NewStore();

        var key = store.Regenerate(ApiKeyScope.Read);

        Assert.Equal(key, store.GetKey(ApiKeyScope.Read));
        Assert.StartsWith("cmpk_", store.GetKey(ApiKeyScope.Control));
    }

    [Fact]
    public void Scopes_AreOrdered_ControlIncludesRead()
    {
        Assert.True(ApiKeys.Allows(ApiKeyScope.Control, ApiKeyScope.Read));
        Assert.True(ApiKeys.Allows(ApiKeyScope.Control, ApiKeyScope.Control));
        Assert.True(ApiKeys.Allows(ApiKeyScope.Read, ApiKeyScope.Read));
        Assert.False(ApiKeys.Allows(ApiKeyScope.Read, ApiKeyScope.Control));
        Assert.True(ApiKeys.TryParseScope("CONTROL", out var scope));
        Assert.Equal(ApiKeyScope.Control, scope);
        Assert.False(ApiKeys.TryParseScope("admin", out _));
    }

    [Fact]
    public void WhenTheDatabaseCannotBeOpened_NothingIsAccepted_AndNoKeyCanBeShown()
    {
        Directory.CreateDirectory(AppPaths.GetStateDbPath());   // a folder where the database file should be
        var store = NewStore();

        Assert.Null(store.Authenticate("cmpk_whatever"));
        Assert.Throws<InvalidOperationException>(() => store.GetKey(ApiKeyScope.Control));
        Assert.Throws<InvalidOperationException>(() => store.Regenerate(ApiKeyScope.Control));
    }

    [Fact]
    public void TheNamedKeysOfTheUnreleasedV3_AreDroppedByTheMigration()
    {
        var resume = AppPaths.GetResumeFilePath();
        NewStore().GetKey(ApiKeyScope.Control);
        // A database as the unreleased 3.0.0-rc.3 build 615c654 left it: schema version 4 with a named-key table.
        var oldFolder = Directory.CreateDirectory(Path.Combine(AppData, "v4")).FullName;
        var oldResume = Path.Combine(oldFolder, "compressarr.resume.json");
        StateDatabase.Snapshot(AppPaths.GetStateDbPath(), StateDatabase.PathFor(oldResume));
        using (var connection = new SqliteConnection($"Data Source={StateDatabase.PathFor(oldResume)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE api_secrets; DROP TABLE auth; DROP TABLE web_sessions; CREATE TABLE api_keys (id INTEGER PRIMARY KEY, name TEXT NOT NULL); PRAGMA user_version = 4;";
            command.ExecuteNonQuery();
        }

        var store = new SqliteApiKeyStore(new SqliteResumeStateStore(), () => oldResume);

        Assert.StartsWith("cmpk_", store.GetKey(ApiKeyScope.Control));   // migrated: the new table is there and the old one is gone
        using var check = new SqliteConnection($"Data Source={StateDatabase.PathFor(oldResume)};Pooling=False");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'api_keys';";
        Assert.Equal(0L, Convert.ToInt64(query.ExecuteScalar()));
        Assert.NotEqual(resume, oldResume);
    }
}
