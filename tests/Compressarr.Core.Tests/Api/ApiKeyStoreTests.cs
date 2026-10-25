using Compressarr.Core.Api;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Api;

/// <summary>The Automation API's keys: made once and shown once, stored only as a hash, found by their text, revoked for good.</summary>
public class ApiKeyStoreTests : AppDataTestBase
{
    private static SqliteApiKeyStore NewStore() => new(new SqliteResumeStateStore());

    [Fact]
    public void ANewKey_LooksLikeAKey_AndIsFoundByItsText()
    {
        var store = NewStore();

        var created = store.Create("Home Assistant", ApiKeyScope.Control);

        Assert.StartsWith("cmpk_", created.Key);
        Assert.Equal(48, created.Key.Length);   // cmpk_ and 43 characters of 256 random bits
        Assert.Matches("^[A-Za-z0-9_-]+$", created.Key);
        var found = store.Authenticate(created.Key);
        Assert.NotNull(found);
        Assert.Equal(created.Info.Id, found!.Id);
        Assert.Equal("Home Assistant", found.Name);
        Assert.Equal(ApiKeyScope.Control, found.Scope);
        Assert.Equal(created.Key[..9], found.Prefix);
    }

    [Fact]
    public void TwoKeys_AreDifferent()
    {
        var store = NewStore();

        var a = store.Create("a", ApiKeyScope.Read);
        var b = store.Create("b", ApiKeyScope.Read);

        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(2, store.List().Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cmpk_notakey")]
    [InlineData("cmpk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void AWrongKey_IsNotAccepted(string key)
    {
        var store = NewStore();
        store.Create("a", ApiKeyScope.Read);

        Assert.Null(store.Authenticate(key));
    }

    [Fact]
    public void AKeyOfAnotherCase_IsNotTheSameKey()
    {
        var store = NewStore();
        var created = store.Create("a", ApiKeyScope.Read);

        Assert.Null(store.Authenticate(created.Key.ToUpperInvariant()));
    }

    [Fact]
    public void OnlyAHashIsStored_NotTheKey()
    {
        var store = NewStore();
        var created = store.Create("a", ApiKeyScope.Read);
        store.List();   // make sure the file is flushed and closed

        var bytes = File.ReadAllBytes(AppPaths.GetStateDbPath());
        var text = System.Text.Encoding.Latin1.GetString(bytes);

        Assert.DoesNotContain(created.Key, text);
        Assert.Contains(ApiKeys.Hash(created.Key), text);
    }

    [Fact]
    public void ARevokedKey_StopsWorkingAtOnce_AndTheOthersKeepWorking()
    {
        var store = NewStore();
        var gone = store.Create("gone", ApiKeyScope.Control);
        var kept = store.Create("kept", ApiKeyScope.Read);

        Assert.True(store.Revoke(gone.Info.Id));

        Assert.Null(store.Authenticate(gone.Key));
        Assert.NotNull(store.Authenticate(kept.Key));
        Assert.False(store.Revoke(gone.Info.Id));
        Assert.Equal("kept", Assert.Single(store.List()).Name);
    }

    [Fact]
    public void LastUsed_StartsEmpty_AndIsNotedWhenTheKeyIsUsed()
    {
        var store = NewStore();
        var created = store.Create("a", ApiKeyScope.Read);
        Assert.Null(store.List().Single().LastUsedAt);

        store.Authenticate(created.Key);

        Assert.NotNull(store.List().Single().LastUsedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AKeyNeedsAName(string name)
    {
        Assert.Throws<ArgumentException>(() => NewStore().Create(name, ApiKeyScope.Read));
    }

    [Fact]
    public void ANameIsTrimmed_AndCannotBeLongerThanSixty()
    {
        var store = NewStore();

        Assert.Equal("padded", store.Create("  padded  ", ApiKeyScope.Read).Info.Name);
        Assert.Throws<ArgumentException>(() => store.Create(new string('x', 61), ApiKeyScope.Read));
        Assert.Equal(60, store.Create(new string('y', 60), ApiKeyScope.Read).Info.Name.Length);
    }

    [Fact]
    public void ThereIsALimitOnHowManyKeysExist()
    {
        var store = NewStore();
        for (var i = 0; i < ApiKeys.MaxKeys; i++) store.Create("k" + i, ApiKeyScope.Read);

        Assert.Throws<InvalidOperationException>(() => store.Create("one too many", ApiKeyScope.Read));
        store.Revoke(store.List()[0].Id);
        store.Create("room again", ApiKeyScope.Read);
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
    public void KeysSurviveARestart_BecauseTheyLiveInTheDatabase()
    {
        var created = NewStore().Create("a", ApiKeyScope.Control);

        Assert.NotNull(NewStore().Authenticate(created.Key));
    }

    [Fact]
    public void WhenTheDatabaseCannotBeOpened_NothingIsAccepted_AndNoKeyCanBeMade()
    {
        Directory.CreateDirectory(AppPaths.GetStateDbPath());   // a folder where the database file should be
        var store = NewStore();

        Assert.Null(store.Authenticate("cmpk_whatever"));
        Assert.Empty(store.List());
        Assert.Throws<InvalidOperationException>(() => store.Create("a", ApiKeyScope.Read));
    }
}
