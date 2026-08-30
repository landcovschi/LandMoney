using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace LandMoney.Web.Auth;

/// <summary>Where the keys that encrypt the authentication cookie are kept -- #88.</summary>
// The same shape as AuthenticationSetup next door: an extension method, so
// Program.cs names the feature instead of growing it.
//
// **What this fixes is an absence, and it is the one #52 left open on purpose.**
// With nothing configured, ASP.NET Core generates the Data Protection key ring in
// memory and writes a warning nobody reads. The keys die with the process, and
// with --min-replicas 0 this process dies after roughly fourteen idle minutes
// (#35) -- so coming back to the site after a pause means typing a password
// again. Two sharper edges of the same cause: a revision replaced mid-session
// signs everybody out, and the day --min-replicas goes above 1 two replicas
// cannot read each other's cookies at all, which stops looking like "signed out
// after a pause" and starts looking like "signed out at random".
public static class DataProtectionSetup
{
    /// <summary>The blob holding the key ring, as a full https URI.</summary>
    public const string BlobUriKey = "DataProtection:KeyRingBlobUri";

    /// <summary>The Key Vault key the ring is encrypted with, as a full https URI.</summary>
    public const string KeyUriKey = "DataProtection:KeyVaultKeyUri";

    /// <summary>
    /// Persists the key ring to blob storage and encrypts it with a Key Vault key,
    /// when both are configured. With neither, the framework default is left alone.
    /// </summary>
    public static IServiceCollection AddLandMoneyDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger logger)
    {
        var policy = ReadKeyRingPolicy(
            configuration[BlobUriKey],
            configuration[KeyUriKey],
            environment.IsDevelopment(),
            logger);

        // Registered whichever branch was taken, so the decision is asked for
        // rather than repeated: Program.cs reads this to know whether there is a
        // key ring worth verifying, and reading the two configuration keys a second
        // time there is how two places come to disagree about which branch ran.
        // RegistrationPolicy next door is registered for the same reason.
        services.AddSingleton(policy);

        if (policy is not PersistedKeyRing persisted)
        {
            // No Data Protection call at all, which is what keeps the ephemeral case
            // the *framework's* default rather than a second implementation of it
            // maintained here.
            return services;
        }

        // One credential for both, and DefaultAzureCredential rather than
        // ManagedIdentityCredential. In the container the two behave identically:
        // Container Apps sets the identity endpoint variables, so the chain finds
        // managed identity without probing IMDS and hanging. The difference is on a
        // developer machine, where DefaultAzureCredential picks up `az login` and
        // the precise one refuses -- and this is the only configuration in the
        // application that cannot be exercised locally at all, so the one route to
        // debugging it is worth keeping open.
        //
        // What that costs, and it is the trap of this class rather than a
        // preference: the chain is ordered and silent about which link answered, so
        // a machine holding a stale AZURE_CLIENT_ID, or an `az login` against the
        // wrong tenant, authenticates as somebody other than the app and the error
        // is a 403 about a role assignment that is in fact correct.
        var credential = new DefaultAzureCredential();

        services
            .AddDataProtection()

            // **Written out rather than defaulted, and leaving it out is a silent
            // sign-out.** The default application discriminator is derived from the
            // content root path and mixed into every purpose string, so two
            // processes sharing a key ring and disagreeing about where they are
            // running cannot read each other's cookies. In this image the path is
            // /app and always has been, which is exactly what makes the default
            // look safe: it holds until something changes the working directory,
            // and then it fails as "everybody signed out" with the key ring intact
            // and blameless.
            .SetApplicationName("LandMoney")

            // Blob first, then vault: the blob is *where*, the vault is *how it is
            // encrypted at rest*. Without the second the key ring is readable XML to
            // anyone who can read the container -- a smaller set than the internet,
            // and not a small enough one for the key that decrypts every session
            // cookie in the application.
            .PersistKeysToAzureBlobStorage(persisted.BlobUri, credential)
            .ProtectKeysWithAzureKeyVault(persisted.KeyUri, credential);

        logger.LogInformation(
            "The Data Protection key ring is persisted to {Blob} and encrypted with {Key}, "
            + "so a restart does not sign anybody out.",
            persisted.BlobUri,
            persisted.KeyUri);

        return services;
    }

    /// <summary>What to do about the key ring, decided from two configuration values.</summary>
    // A pure function taking strings rather than an IConfiguration, for the reason
    // RegistrationPolicy is one: the decision is the part with an argument in it and
    // the part worth testing. Everything around it is one call into a package.
    public static KeyRingPolicy ReadKeyRingPolicy(
        string? blobUri,
        string? keyUri,
        bool isDevelopment,
        ILogger logger)
    {
        var hasBlob = !string.IsNullOrWhiteSpace(blobUri);
        var hasKey = !string.IsNullOrWhiteSpace(keyUri);

        if (!hasBlob && !hasKey)
        {
            // The state a developer machine and `efbundle` are both in, and it has
            // to stay legal for both. #57 is what a required-configuration throw on
            // the bundle's path costs: it runs Program.cs from a directory holding
            // nothing but itself, so every key is missing there and always will be.
            //
            // Development says nothing worth an error. Keys in memory are the right
            // answer on a machine restarted by its owner, on purpose, while they are
            // looking at it.
            if (isDevelopment)
            {
                logger.LogInformation(
                    "No {BlobKey} is configured, so the Data Protection key ring lives in memory "
                    + "and a restart signs this machine out. That is the right answer locally.",
                    BlobUriKey);
            }
            else
            {
                // Not a throw, for the reason above -- and not silence either,
                // because silence is precisely the bug. This is the deployed
                // application quietly back in the state #88 was opened about, and
                // the only other thing between here and that state is the assertion
                // ci.yml makes on every deployment.
                logger.LogError(
                    "Neither {BlobKey} nor {KeyKey} is configured and this is not the Development "
                    + "environment, so the Data Protection key ring lives in memory: every restart "
                    + "signs everybody out, and two replicas cannot read each other's cookies. "
                    + "See step 15 of docs/deploy-azure.md.",
                    BlobUriKey,
                    KeyUriKey);
            }

            return EphemeralKeyRing.Instance;
        }

        // **Half configured is an error, and it is the one throw in this file.** It
        // is safe against #57 for a reason worth stating rather than assuming: the
        // bundle has *neither* key, so it takes the branch above and never reaches
        // this one. Arriving here means somebody set one of the two, which is a
        // mistake to report rather than a state to tolerate -- the same call
        // Program.cs makes about a Categorizer:BaseUrl that is present and
        // unparseable.
        //
        // The two halves are not symmetrical, and the dangerous one is the half that
        // works. A vault key with no blob is nonsense: there is nothing to encrypt,
        // and it fails at once. A blob with no vault starts, persists, keeps
        // everybody signed in, and leaves the key that decrypts every cookie lying
        // in a container as plain XML -- a downgrade nothing would report. So the
        // two arrive together or not at all.
        if (!hasBlob || !hasKey)
        {
            throw new InvalidOperationException(
                $"Only one of {BlobUriKey} and {KeyUriKey} is set. The key ring is persisted and "
                + "encrypted together or not at all: with the blob alone it would be stored as "
                + "readable XML, and with the key alone there is nothing to encrypt. "
                + "Set both, or neither. See step 15 of docs/deploy-azure.md.");
        }

        return new PersistedKeyRing(
            Absolute(blobUri!, BlobUriKey),
            Absolute(keyUri!, KeyUriKey));
    }

    private static Uri Absolute(string value, string key) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException(
                $"{key} is '{value}', which is not an absolute URI. It wants the full https "
                + "address of the blob or of the key, not a name.");

    /// <summary>
    /// Refuses to carry on when a key already in the store cannot be read, rather
    /// than letting the framework quietly replace it.
    /// </summary>
    // **This is the half of #88 that is not two package references, and it exists
    // because the framework's own answer to an unreadable key is to make a new
    // one.**
    //
    // The path is not obvious and is worth writing down. XmlKeyManager reads the key
    // ring XML and hands each entry to DefaultKeyResolver, which asks whether the
    // key can produce an encryptor. Answering that involves Key Vault, because the
    // descriptor is encrypted with it -- and DefaultKeyResolver catches every
    // exception the attempt throws, logs it at Warning, and treats the key as
    // ineligible. With no eligible key left, KeyRingProvider does what it does on a
    // brand-new installation: it generates one. So a vault that has revoked this
    // application's access, or a key somebody deleted, does not produce an outage.
    // It produces a fresh key ring, a working site, and everybody signed out --
    // which is the bug #88 is about, arriving through the fix for it.
    //
    // Reading the whole ring once at startup is what turns that back into a failure.
    // GetAllKeys() surfaces the store being unreachable and asking each key for an
    // encryptor surfaces the vault refusing to unwrap it. Neither is swallowed here.
    //
    // What it costs, said out loud: with --min-replicas 0 this runs on every cold
    // start, so an Azure blip during a wake-up is a replica that fails to start
    // rather than one that serves. That is the trade #88 asks for in as many words,
    // and the alternative is the failure that looks exactly like success. The Azure
    // SDK retries a transient error three times underneath this before it ever
    // reaches this method.
    //
    // It runs only when the ring is persisted, so nothing about a developer machine
    // or `efbundle` changes: there is no key store to ask either of them about.
    public static void VerifyKeyRing(IKeyManager keyManager, ILogger logger)
    {
        var keys = keyManager.GetAllKeys();

        var unreadable = 0;

        foreach (var key in keys)
        {
            // A revoked key is a deliberate act and is meant to be unusable, so it
            // is not evidence of anything being wrong.
            if (key.IsRevoked)
            {
                continue;
            }

            try
            {
                key.CreateEncryptor();
            }
            catch (Exception exception)
            {
                unreadable++;

                // Logged per key and thrown once. An exception carries one message;
                // a ring with three unreadable keys has three reasons, and the
                // second and third are what say whether this is one deleted key or a
                // vault nobody can reach.
                logger.LogError(
                    exception,
                    "Data Protection key {KeyId} is in the store and cannot be decrypted.",
                    key.KeyId);
            }
        }

        if (unreadable > 0)
        {
            throw new InvalidOperationException(
                $"{unreadable} of {keys.Count} Data Protection keys are in the store and cannot "
                + "be decrypted. Carrying on would generate a fresh key ring and sign everybody "
                + $"out, so this is a refusal instead. Check this application's access to "
                + $"{KeyUriKey}.");
        }

        logger.LogInformation(
            "The Data Protection key ring holds {Count} keys and every one of them can be read.",
            keys.Count);
    }
}

/// <summary>What was decided about the key ring.</summary>
// Two cases and no boolean, so "persisted" and "the two URIs" cannot disagree:
// there is no way to hold one without the other. RegistrationPolicy keeps its flag
// separate from its value for the opposite reason -- there, three states matter.
public abstract record KeyRingPolicy;

/// <summary>Keys in memory, dying with the process. Right locally, a bug deployed.</summary>
public sealed record EphemeralKeyRing : KeyRingPolicy
{
    public static readonly EphemeralKeyRing Instance = new();
}

/// <summary>Keys in a blob, encrypted with a Key Vault key.</summary>
public sealed record PersistedKeyRing(Uri BlobUri, Uri KeyUri) : KeyRingPolicy;
