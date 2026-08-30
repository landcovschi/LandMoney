using LandMoney.Web.Auth;
using LandMoney.Web.Tests.Categorizing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LandMoney.Web.Tests.Auth;

/// <summary>Where the Data Protection keys go, which is the one decision in #88.</summary>
// A pure function over two strings, for the reason RegistrationPolicyTests exists
// next door: the part with an argument in it is testable, and everything around it
// is one call into a package that Azure would have to answer.
//
// What is deliberately NOT here: that PersistKeysToAzureBlobStorage and
// ProtectKeysWithAzureKeyVault do what they say. Both need a storage account, a
// vault, an identity and a network, which is the wall #22's "no Postgres, no
// Docker and no network" puts in the same place every time. They are verified by
// hand against the deployed application, and step 15 of docs/deploy-azure.md is
// the run.
public class KeyRingPolicyTests
{
    private static readonly RecordingLogger<KeyRingPolicy> Silent = new();

    private const string Blob = "https://stlandmoneypl.blob.core.windows.net/keyring/keys.xml";
    private const string Key = "https://kv-landmoney-pl.vault.azure.net/keys/dataprotection";

    // The everyday loop and `efbundle`, which are the same state seen from two
    // places. Neither may be made to configure anything, and neither may throw.
    [Fact]
    public void Nothing_configured_on_a_developer_machine_keeps_the_keys_in_memory()
    {
        var logger = new RecordingLogger<KeyRingPolicy>();

        var policy = DataProtectionSetup.ReadKeyRingPolicy(
            blobUri: null, keyUri: null, isDevelopment: true, logger);

        Assert.IsType<EphemeralKeyRing>(policy);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
    }

    // The same answer with a different noise level, and the difference is the whole
    // of what #88 can do about this state at runtime. It cannot throw -- efbundle
    // runs Program.cs from a directory holding no configuration at all, and #57 is
    // what a required-configuration throw on that path costs -- so an error in the
    // log plus ci.yml's assertion on the deployed app is what is left.
    [Fact]
    public void Nothing_configured_anywhere_else_is_an_error_and_still_starts()
    {
        var logger = new RecordingLogger<KeyRingPolicy>();

        var policy = DataProtectionSetup.ReadKeyRingPolicy(
            blobUri: null, keyUri: null, isDevelopment: false, logger);

        Assert.IsType<EphemeralKeyRing>(policy);

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);

        // The fields rather than the sentence, per RecordingLogger's own note: a
        // reworded message is not a behaviour change, and a message that stops
        // naming the keys is one, because the key names are the whole remedy.
        Assert.Equal(DataProtectionSetup.BlobUriKey, error.Field("BlobKey"));
        Assert.Equal(DataProtectionSetup.KeyUriKey, error.Field("KeyKey"));
    }

    [Fact]
    public void Both_configured_is_a_persisted_ring_at_exactly_those_two_addresses()
    {
        var policy = DataProtectionSetup.ReadKeyRingPolicy(Blob, Key, isDevelopment: false, Silent);

        var persisted = Assert.IsType<PersistedKeyRing>(policy);
        Assert.Equal(new Uri(Blob), persisted.BlobUri);
        Assert.Equal(new Uri(Key), persisted.KeyUri);
    }

    // Development does not open a second route. A machine that has been pointed at
    // the real store -- which is the only way to debug this at all, since none of it
    // can be exercised locally -- gets the real store.
    [Fact]
    public void Development_does_not_override_a_ring_that_was_configured_on_purpose()
    {
        var policy = DataProtectionSetup.ReadKeyRingPolicy(Blob, Key, isDevelopment: true, Silent);

        Assert.IsType<PersistedKeyRing>(policy);
    }

    // The half that works is the dangerous one, and it is why this is a refusal
    // rather than a warning. A blob with no vault starts, persists, keeps everybody
    // signed in, and leaves the key that decrypts every session cookie in a
    // container as readable XML -- a downgrade with no symptom at all.
    [Theory]
    [InlineData(Blob, null)]
    [InlineData(Blob, "")]
    [InlineData(Blob, "   ")]
    [InlineData(null, Key)]
    [InlineData("", Key)]
    public void One_of_the_two_alone_is_refused(string? blobUri, string? keyUri)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionSetup.ReadKeyRingPolicy(blobUri, keyUri, isDevelopment: false, Silent));

        // Both keys named, because the message has to say what to add as well as
        // what is wrong, and which of the two is missing depends on the case.
        Assert.Contains(DataProtectionSetup.BlobUriKey, thrown.Message);
        Assert.Contains(DataProtectionSetup.KeyUriKey, thrown.Message);
    }

    // Blank is absent and not half-configured. An environment variable set to the
    // empty string is what a Container Apps variable filled from a deleted secret
    // looks like, and reading that as "one of the two is set" would turn a missing
    // value into a process that will not start.
    [Fact]
    public void Both_blank_is_the_same_as_both_missing()
    {
        var policy = DataProtectionSetup.ReadKeyRingPolicy(
            "   ", string.Empty, isDevelopment: false, Silent);

        Assert.IsType<EphemeralKeyRing>(policy);
    }

    // Present and unusable throws, which is the same call Program.cs makes about a
    // Categorizer:BaseUrl that is present and not a URI: a value somebody typed and
    // got wrong is a mistake to report, not a state to tolerate. The container name
    // alone is the mistake this actually catches -- it looks like a setting and
    // resolves to nothing.
    //
    // **The `/keys/dataprotection` row is the one that earned this theory its
    // scheme check, and it did it by being green here and red in CI.** On Windows a
    // leading slash is not an absolute URI; on Unix it is an absolute *file* path,
    // so `Uri.TryCreate(..., UriKind.Absolute, ...)` answers true and produces
    // `file:///keys/dataprotection`. The deployed container is Linux, so the check
    // as first written would have accepted a path in the one environment that
    // matters and refused it on the machine it was written on.
    [Theory]
    [InlineData("keyring/keys.xml", Key)]
    [InlineData("stlandmoneypl", Key)]
    [InlineData(Blob, "dataprotection")]
    [InlineData(Blob, "/keys/dataprotection")]
    [InlineData("/keyring/keys.xml", Key)]
    [InlineData(Blob, "file:///keys/dataprotection")]

    // http is refused too, although it parses perfectly. Neither endpoint speaks
    // it, so accepting one would only move the failure to the first wrap.
    [InlineData("http://stlandmoneypl.blob.core.windows.net/keyring/keys.xml", Key)]
    public void A_value_that_is_not_an_https_uri_is_refused(string blobUri, string keyUri)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionSetup.ReadKeyRingPolicy(blobUri, keyUri, isDevelopment: false, Silent));

        Assert.Contains("not an https URI", thrown.Message);
    }

    // **The application name is the line most likely to be deleted as noise**, and
    // its absence is a silent sign-out rather than an error: the default
    // discriminator is derived from the content root path and mixed into every
    // purpose string, so two processes sharing one key ring and disagreeing about
    // where they run cannot read each other's cookies. In this image the path is
    // /app and always has been, which is exactly what makes leaving it out look
    // safe.
    //
    // Registration only -- nothing here resolves a key or opens a socket.
    [Fact]
    public void A_persisted_ring_pins_the_application_name_rather_than_the_content_root()
    {
        var services = Register(Blob, Key);

        Assert.Equal(
            "LandMoney",
            services.GetRequiredService<IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator);
    }

    // The contrast, and it is what says the branch above was actually taken rather
    // than the name being set unconditionally: with nothing configured this file
    // touches Data Protection at all, so the discriminator is still whatever the
    // framework derived.
    [Fact]
    public void Nothing_configured_leaves_the_framework_default_alone()
    {
        var services = Register(blobUri: null, keyUri: null);

        Assert.NotEqual(
            "LandMoney",
            services.GetRequiredService<IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator);
    }

    private static ServiceProvider Register(string? blobUri, string? keyUri)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataProtectionSetup.BlobUriKey] = blobUri,
                [DataProtectionSetup.KeyUriKey] = keyUri,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLandMoneyDataProtection(configuration, new TestEnvironment(), Silent);

        return services.BuildServiceProvider();
    }

    /// <summary>Production, and a content root nothing reads.</summary>
    // AddLandMoneyDataProtection asks the environment one question -- whether this
    // is Development -- and IsDevelopment() is an extension method over
    // EnvironmentName, so that is the only property here with a value that matters.
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "LandMoney.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();

        public IFileProvider WebRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    // The one assertion here made against the assembled application rather than
    // against the function, and it is the property #88 must not break: `dotnet run`
    // and `efbundle` both reach this file with neither key set, and both have to
    // come up. It also checks the registration itself -- Program.cs decides whether
    // to verify the ring by asking the container for this policy, so a branch that
    // forgot to register it would resolve nothing and take the application down at
    // startup rather than at a request.
    [Fact]
    public void The_assembled_application_starts_with_no_key_ring_configured()
    {
        using var app = TestApp.WithInviteCode();

        // The host is built lazily; asking for a client is what builds it, and a
        // throw anywhere in Program.cs surfaces here.
        using var client = app.CreateNonFollowingClient();

        Assert.IsType<EphemeralKeyRing>(app.Services.GetRequiredService<KeyRingPolicy>());
    }
}
