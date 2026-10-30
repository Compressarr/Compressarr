using System.Text;
using Compressarr.Core.Auth;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Auth;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class AuthTestClock : TimeProvider
{
    private DateTimeOffset _now;
    public AuthTestClock(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public class PasswordHasherTests
{
    [Fact]
    public void AHash_HasTheStoredFormat_AndVerifies()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        var parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha512", parts[0]);
        Assert.Equal("220000", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("correct horse battery stapl", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    [Fact]
    public void TwoHashesOfOnePassword_DifferBecauseOfTheSalt_ButBothVerify()
    {
        var a = PasswordHasher.Hash("same password");
        var b = PasswordHasher.Hash("same password");

        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("same password", a));
        Assert.True(PasswordHasher.Verify("same password", b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("pbkdf2-sha512$220000$not base64$also not")]
    [InlineData("pbkdf2-sha512$0$AAAA$AAAA")]
    [InlineData("pbkdf2-sha512$220000$$")]
    [InlineData("pbkdf2-sha256$220000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void AMalformedHash_NeverMatches(string? stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
        Assert.True(PasswordHasher.NeedsRehash(stored));
    }

    [Fact]
    public void AnOlderHash_StillVerifies_AndIsDueForReplacing()
    {
        var older = PasswordHasher.Hash("old password", 1000);

        Assert.True(PasswordHasher.Verify("old password", older));
        Assert.True(PasswordHasher.NeedsRehash(older));
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Hash("new password")));
    }

    [Fact]
    public void ThePasswordIsNeverInTheHash()
    {
        Assert.DoesNotContain("hunter2hunter2", PasswordHasher.Hash("hunter2hunter2"));
    }
}

public class AttemptThrottleTests
{
    private readonly AuthTestClock _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private AttemptThrottle New() => new(5, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), _time);

    [Fact]
    public void FiveWrongAttempts_LockTheAddressForFiveMinutes_AndTheFifthSaysSo()
    {
        var throttle = New();
        for (var i = 0; i < 4; i++) Assert.False(throttle.Failed("a"));
        Assert.Null(throttle.RetryAfter("a"));

        Assert.True(throttle.Failed("a"));

        Assert.Equal(TimeSpan.FromMinutes(5), throttle.RetryAfter("a"));
        Assert.False(throttle.Failed("a"));   // already locked: not announced again
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(TimeSpan.FromMinutes(2), throttle.RetryAfter("a"));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(throttle.RetryAfter("a"));
    }

    [Fact]
    public void OtherAddresses_AreNotAffected()
    {
        var throttle = New();
        for (var i = 0; i < 5; i++) throttle.Failed("a");

        Assert.NotNull(throttle.RetryAfter("a"));
        Assert.Null(throttle.RetryAfter("b"));
    }

    [Fact]
    public void ASuccess_ClearsTheCount()
    {
        var throttle = New();
        for (var i = 0; i < 4; i++) throttle.Failed("a");

        throttle.Succeeded("a");

        for (var i = 0; i < 4; i++) Assert.False(throttle.Failed("a"));   // counting from zero again
        Assert.Null(throttle.RetryAfter("a"));
    }

    [Fact]
    public void FailuresSpreadOverMoreThanTheWindow_DoNotAddUp()
    {
        var throttle = New();
        for (var i = 0; i < 4; i++) throttle.Failed("a");
        _time.Advance(TimeSpan.FromMinutes(6));

        Assert.False(throttle.Failed("a"));
        Assert.Null(throttle.RetryAfter("a"));
    }
}

/// <summary>The account, the sessions and the way back, in the state database.</summary>
public class WebAuthTests : AppDataTestBase
{
    private readonly AuthTestClock _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private SqliteWebAuth New() => new(new SqliteResumeStateStore(), AppPaths.GetResumeFilePath, _time);

    private SqliteWebAuth WithAccount(AuthMethod method = AuthMethod.Forms, AuthRequired required = AuthRequired.Enabled)
    {
        var auth = New();
        auth.SaveSettings(method, required, false, "Mark", "a long password phrase");
        return auth;
    }

    // ---- settings ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NothingSavedYet_IsTheDefault_FormsWithTheLocalNetworkFreeAndNoAccount()
    {
        var settings = New().GetSettings();

        Assert.Equal(AuthSettings.Default, settings);
    }

    [Fact]
    public void SavedSettings_AreRemembered_TheUsernameIsLowerCase_AndThePasswordIsNotReturned()
    {
        New().SaveSettings(AuthMethod.Basic, AuthRequired.DisabledForLocalhost, true, "  MarkW ", "twelve chars!!");

        var read = New().GetSettings();

        Assert.Equal(new AuthSettings(AuthMethod.Basic, AuthRequired.DisabledForLocalhost, true, "markw", true), read);
        Assert.DoesNotContain("twelve chars!!", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(AppPaths.GetStateDbPath())));
    }

    [Fact]
    public void SavingSettingsWithNoAccount_IsFine_ItIsTheStateBeforeSetup()
    {
        var saved = New().SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, null, null);

        Assert.False(saved.CredentialsSet);
        Assert.Equal(AuthRequired.Enabled, New().GetSettings().Required);
    }

    [Theory]
    [InlineData("user", "short")]                    // under 8 characters
    [InlineData("user", "USER")]                     // not the username (also too short)
    [InlineData("longusername", "LongUserName")]     // the same as the username, in another case
    [InlineData("", "a long password phrase")]       // a password with no username
    public void ABadAccount_IsRefused_WithAMessage_AndNothingIsSaved(string username, string password)
    {
        var auth = New();

        var ex = Assert.Throws<ArgumentException>(() => auth.SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, username, password));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(AuthSettings.Default, auth.GetSettings());
    }

    [Fact]
    public void AUsernameWithoutAPassword_IsRefused_WhenThereIsNoneYet()
    {
        Assert.Throws<ArgumentException>(() => New().SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, "mark", null));
        Assert.Throws<ArgumentException>(() => New().SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, new string('x', 65), "a long password phrase"));
        Assert.Throws<ArgumentException>(() => New().SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, "bad\nname", "a long password phrase"));
    }

    [Fact]
    public void ABlankPassword_KeepsTheOne_AndABlankUsernameKeepsTheName()
    {
        var auth = WithAccount();

        auth.SaveSettings(AuthMethod.Forms, AuthRequired.DisabledForLocalAddresses, false, "", "");

        var read = auth.GetSettings();
        Assert.Equal(("mark", true, AuthRequired.DisabledForLocalAddresses), (read.Username, read.HasPassword, read.Required));
        Assert.Equal(LoginOutcome.Success, auth.Login("Mark", "a long password phrase", "10.0.0.5", false).Outcome);
    }

    // ---- login ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARightLogin_MakesASession_ThatValidates_AndTheUsernameIsNotCaseSensitive()
    {
        var auth = WithAccount();

        var result = auth.Login("MARK", "a long password phrase", "203.0.113.5", remember: true);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.True(result.Persistent);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromDays(7), result.Expires);
        var session = auth.ValidateSession(result.Token!);
        Assert.Equal(("mark", true), (session!.Username, session.Persistent));
    }

    [Fact]
    public void WithoutRememberMe_TheSessionIsNotPersistent()
    {
        var auth = WithAccount();

        var result = auth.Login("mark", "a long password phrase", "203.0.113.5", remember: false);

        Assert.False(result.Persistent);
        Assert.False(auth.ValidateSession(result.Token!)!.Persistent);
    }

    [Theory]
    [InlineData("mark", "wrong password!!")]
    [InlineData("someone", "a long password phrase")]
    [InlineData("", "")]
    public void AWrongLogin_IsWrongCredentials_NeverSayingWhichHalf(string username, string password)
    {
        var auth = WithAccount();

        var result = auth.Login(username, password, "203.0.113.5", false);

        Assert.Equal(LoginOutcome.WrongCredentials, result.Outcome);
        Assert.Null(result.Token);
    }

    [Fact]
    public void LoginIsNotAvailable_WithNoAccount_OrWhenTheMethodIsNotForms()
    {
        Assert.Equal(LoginOutcome.NotAvailable, New().Login("mark", "a long password phrase", "10.0.0.1", false).Outcome);

        var basic = WithAccount(AuthMethod.Basic);
        Assert.Equal(LoginOutcome.NotAvailable, basic.Login("mark", "a long password phrase", "10.0.0.1", false).Outcome);
    }

    [Fact]
    public void FiveWrongLogins_LockTheAddress_EvenAgainstTheRightPassword_ForFiveMinutes()
    {
        var auth = WithAccount();
        LoginResult last = null!;
        for (var i = 0; i < 5; i++) last = auth.Login("mark", "wrong password!!", "203.0.113.5", false);

        Assert.Equal(LoginOutcome.WrongCredentials, last.Outcome);
        Assert.NotNull(last.RetryAfter);   // the fifth one tells the caller the address is now locked
        var locked = auth.Login("mark", "a long password phrase", "203.0.113.5", false);
        Assert.Equal(LoginOutcome.LockedOut, locked.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), locked.RetryAfter);
        Assert.Equal(TimeSpan.FromMinutes(5), auth.LockedFor("203.0.113.5"));
        Assert.Equal(LoginOutcome.Success, auth.Login("mark", "a long password phrase", "203.0.113.99", false).Outcome);   // another address is fine

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(LoginOutcome.Success, auth.Login("mark", "a long password phrase", "203.0.113.5", false).Outcome);
    }

    [Fact]
    public void AnOlderHash_IsReplacedAtTheNextSuccessfulLogin()
    {
        var auth = WithAccount();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={AppPaths.GetStateDbPath()};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE auth SET password_hash = @hash;";
            command.Parameters.AddWithValue("@hash", PasswordHasher.Hash("a long password phrase", 1000));
            command.ExecuteNonQuery();
        }

        Assert.Equal(LoginOutcome.Success, auth.Login("mark", "a long password phrase", "10.0.0.1", false).Outcome);

        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={AppPaths.GetStateDbPath()};Pooling=False");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT password_hash FROM auth;";
        var stored = (string)query.ExecuteScalar()!;
        Assert.StartsWith("pbkdf2-sha512$220000$", stored);
        Assert.False(PasswordHasher.NeedsRehash(stored));
        Assert.Equal(LoginOutcome.Success, auth.Login("mark", "a long password phrase", "10.0.0.1", false).Outcome);   // and it still verifies
    }

    // ---- Basic ---------------------------------------------------------------------------------------------------------------------------

    private static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public void Basic_AcceptsTheRightCredentials_AndRefusesTheWrongOnes()
    {
        var auth = WithAccount(AuthMethod.Basic);

        Assert.Equal(LoginOutcome.Success, auth.AuthenticateBasic(Basic("mark", "a long password phrase"), "203.0.113.5").Outcome);
        Assert.Equal(LoginOutcome.Success, auth.AuthenticateBasic(Basic("MARK", "a long password phrase"), "203.0.113.5").Outcome);
        Assert.Equal(LoginOutcome.WrongCredentials, auth.AuthenticateBasic(Basic("mark", "nope nope nope"), "203.0.113.5").Outcome);
    }

    [Fact]
    public void Basic_APasswordWithAColon_IsKeptWhole()
    {
        var auth = New();
        auth.SaveSettings(AuthMethod.Basic, AuthRequired.Enabled, false, "mark", "pass:word:with:colons");

        Assert.Equal(LoginOutcome.Success, auth.AuthenticateBasic(Basic("mark", "pass:word:with:colons"), "203.0.113.5").Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("Basic")]
    [InlineData("Basic !!!not-base64!!!")]
    public void Basic_NoUsableHeader_IsNoCredentials_AndIsNotAFailedAttempt(string? header)
    {
        var auth = WithAccount(AuthMethod.Basic);

        for (var i = 0; i < 10; i++) Assert.Equal(LoginOutcome.NoCredentials, auth.AuthenticateBasic(header, "203.0.113.5").Outcome);

        Assert.Null(auth.LockedFor("203.0.113.5"));
    }

    [Fact]
    public void Basic_IsNotAvailable_WithNoAccount_OrWhenTheMethodIsForms()
    {
        Assert.Equal(LoginOutcome.NotAvailable, New().AuthenticateBasic(Basic("a", "b"), "10.0.0.1").Outcome);
        Assert.Equal(LoginOutcome.NotAvailable, WithAccount(AuthMethod.Forms).AuthenticateBasic(Basic("mark", "a long password phrase"), "10.0.0.1").Outcome);
    }

    [Fact]
    public void Basic_FiveWrongAttempts_LockTheAddressToo()
    {
        var auth = WithAccount(AuthMethod.Basic);
        for (var i = 0; i < 5; i++) auth.AuthenticateBasic(Basic("mark", "wrong password!!"), "203.0.113.5");

        Assert.Equal(LoginOutcome.LockedOut, auth.AuthenticateBasic(Basic("mark", "a long password phrase"), "203.0.113.5").Outcome);
    }

    // ---- the current password ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CheckCurrentPassword_AcceptsTheRightOne_AndCountsTheWrongOnesAgainstTheAddress()
    {
        var auth = WithAccount();

        Assert.True(auth.CheckCurrentPassword("a long password phrase", "203.0.113.5"));
        for (var i = 0; i < 5; i++) Assert.False(auth.CheckCurrentPassword("wrong password!!", "203.0.113.5"));

        Assert.False(auth.CheckCurrentPassword("a long password phrase", "203.0.113.5"));   // locked now
        Assert.True(auth.CheckCurrentPassword("a long password phrase", "203.0.113.6"));
    }

    [Fact]
    public void CheckCurrentPassword_IsFalse_WhenThereIsNoAccount() =>
        Assert.False(New().CheckCurrentPassword("anything at all", "10.0.0.1"));

    // ---- sessions ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASession_SlidesAWeekFromEachUse_AndLapsesAfterAWeekUnused()
    {
        var auth = WithAccount();
        var token = auth.Login("mark", "a long password phrase", "10.0.0.1", false).Token!;

        _time.Advance(TimeSpan.FromDays(6));
        var used = auth.ValidateSession(token);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromDays(7), used!.Expires);

        _time.Advance(TimeSpan.FromDays(6));
        Assert.NotNull(auth.ValidateSession(token));   // twelve days in, but used six days ago

        _time.Advance(TimeSpan.FromDays(8));
        Assert.Null(auth.ValidateSession(token));
        Assert.Null(auth.ValidateSession(token));      // and it was deleted, not just hidden
    }

    [Fact]
    public void ATokenThatWasNeverIssued_IsNotASession()
    {
        var auth = WithAccount();

        Assert.Null(auth.ValidateSession("not-a-token"));
        Assert.Null(auth.ValidateSession(""));
        Assert.Null(auth.ValidateSession(new string('x', 500)));
    }

    [Fact]
    public void OnlyAHashOfTheToken_IsStored()
    {
        var auth = WithAccount();
        var token = auth.Login("mark", "a long password phrase", "10.0.0.1", false).Token!;

        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(AppPaths.GetStateDbPath()));

        Assert.DoesNotContain(token, text);
    }

    [Fact]
    public void SigningOut_EndsThatSession_Only()
    {
        var auth = WithAccount();
        var a = auth.Login("mark", "a long password phrase", "10.0.0.1", false).Token!;
        var b = auth.Login("mark", "a long password phrase", "10.0.0.2", false).Token!;

        auth.EndSession(a);

        Assert.Null(auth.ValidateSession(a));
        Assert.NotNull(auth.ValidateSession(b));
    }

    [Fact]
    public void SignOutEverywhere_EndsAll_OrAllButTheOneGiven()
    {
        var auth = WithAccount();
        var tokens = Enumerable.Range(0, 3).Select(i => auth.Login("mark", "a long password phrase", $"10.0.0.{i}", false).Token!).ToList();

        Assert.Equal(2, auth.EndAllSessions(exceptToken: tokens[1]));
        Assert.Null(auth.ValidateSession(tokens[0]));
        Assert.NotNull(auth.ValidateSession(tokens[1]));
        Assert.Null(auth.ValidateSession(tokens[2]));
        Assert.Equal(1, auth.EndAllSessions());
        Assert.Null(auth.ValidateSession(tokens[1]));
    }

    [Fact]
    public void ChangingThePassword_EndsEverySession()
    {
        var auth = WithAccount();
        var token = auth.Login("mark", "a long password phrase", "10.0.0.1", false).Token!;

        auth.SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, "mark", "a different long phrase");

        Assert.Null(auth.ValidateSession(token));
        Assert.Equal(LoginOutcome.WrongCredentials, auth.Login("mark", "a long password phrase", "10.0.0.1", false).Outcome);
        Assert.Equal(LoginOutcome.Success, auth.Login("mark", "a different long phrase", "10.0.0.1", false).Outcome);
    }

    [Fact]
    public void ChangingTheUsername_EndsTheOldSignIn_AtItsNextUse()
    {
        var auth = WithAccount();
        var token = auth.Login("mark", "a long password phrase", "10.0.0.1", false).Token!;

        auth.SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, "mark2", "");

        Assert.Null(auth.ValidateSession(token));
    }

    [Fact]
    public void ANewLogin_PurgesLapsedSessions()
    {
        var auth = WithAccount();
        var old = auth.Login("mark", "a long password phrase", "10.0.0.1", false).Token!;
        _time.Advance(TimeSpan.FromDays(9));

        auth.Login("mark", "a long password phrase", "10.0.0.1", false);

        Assert.Null(auth.ValidateSession(old));
    }

    // ---- the way back --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ResetToLocalOnly_ReturnsToDisabledForLocalAddresses_KeepingTheAccount()
    {
        var auth = WithAccount(AuthMethod.Forms, AuthRequired.Enabled);

        auth.ResetToLocalOnly();

        var read = auth.GetSettings();
        Assert.Equal((AuthRequired.DisabledForLocalAddresses, AuthMethod.Forms, "mark", true), (read.Required, read.Method, read.Username, read.HasPassword));
    }

    [Fact]
    public void ResetToLocalOnly_WorksBeforeAnythingWasSaved()
    {
        var auth = New();

        auth.ResetToLocalOnly();

        Assert.Equal(AuthSettings.Default, auth.GetSettings());
    }

    [Fact]
    public void TheEnvironmentVariable_DoesTheResetOnce_OnlyWhenItIsOne()
    {
        var auth = WithAccount(AuthMethod.Forms, AuthRequired.Enabled);
        var before = Environment.GetEnvironmentVariable(SqliteWebAuth.ResetVariable);
        try
        {
            Environment.SetEnvironmentVariable(SqliteWebAuth.ResetVariable, null);
            Assert.False(auth.ApplyEnvironmentReset());
            Assert.Equal(AuthRequired.Enabled, auth.GetSettings().Required);

            Environment.SetEnvironmentVariable(SqliteWebAuth.ResetVariable, "0");
            Assert.False(auth.ApplyEnvironmentReset());

            Environment.SetEnvironmentVariable(SqliteWebAuth.ResetVariable, "1");
            Assert.True(auth.ApplyEnvironmentReset());
            Assert.Equal(AuthRequired.DisabledForLocalAddresses, auth.GetSettings().Required);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SqliteWebAuth.ResetVariable, before);
        }
    }

    // ---- when the database is not there --------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenTheDatabaseCannotBeOpened_OnlyThisComputerIsFree_NeverFreerThanTheSavedSettingsMightHaveBeen()
    {
        Directory.CreateDirectory(AppPaths.GetStateDbPath());   // a folder where the database file should be
        var auth = New();

        var settings = auth.GetSettings();
        Assert.True(settings.IsUnavailable);
        Assert.False(WebAuthPolicy.IsFree(settings, System.Net.IPAddress.Parse("192.168.1.5"), hasForwardingHeader: false));   // a LAN peer is not let in by the fault
        Assert.False(WebAuthPolicy.IsFree(settings, System.Net.IPAddress.Parse("203.0.113.9"), hasForwardingHeader: false));
        Assert.True(WebAuthPolicy.IsFree(settings, System.Net.IPAddress.Loopback, hasForwardingHeader: false));                // this computer still is
        Assert.Equal(LoginOutcome.NotAvailable, auth.Login("mark", "a long password phrase", "10.0.0.1", false).Outcome);
        Assert.Null(auth.ValidateSession("anything"));
        Assert.Throws<InvalidOperationException>(() => auth.SaveSettings(AuthMethod.Forms, AuthRequired.Enabled, false, "mark", "a long password phrase"));
    }

    // ---- the backup ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ABackupSnapshot_KeepsTheAccount_ButDropsTheSessions()
    {
        var auth = WithAccount();
        var token = auth.Login("mark", "a long password phrase", "10.0.0.1", false).Token!;
        var copy = Path.Combine(AppData, "snapshot.db");

        Assert.True(StateDatabase.Snapshot(AppPaths.GetStateDbPath(), copy));

        Assert.NotNull(auth.ValidateSession(token));   // the live database is untouched
        var restoredFolder = Directory.CreateDirectory(Path.Combine(AppData, "restored")).FullName;
        var restoredResume = Path.Combine(restoredFolder, "compressarr.resume.json");
        File.Copy(copy, StateDatabase.PathFor(restoredResume));
        var restored = new SqliteWebAuth(new SqliteResumeStateStore(), () => restoredResume, _time);
        Assert.Equal("mark", restored.GetSettings().Username);
        Assert.Equal(LoginOutcome.Success, restored.Login("mark", "a long password phrase", "10.0.0.1", false).Outcome);
        Assert.Null(restored.ValidateSession(token));  // nobody stays signed in through a restore
    }
}
