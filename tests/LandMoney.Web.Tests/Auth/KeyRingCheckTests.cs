using LandMoney.Web.Auth;
using LandMoney.Web.Tests.Categorizing;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Logging;

namespace LandMoney.Web.Tests.Auth;

/// <summary>The refusal that stands between an unreadable key and a silent sign-out.</summary>
// **Why this exists at all is a measurement rather than a suspicion**, taken while
// writing #88 against the real framework with a file-system store and certificate
// protection standing in for blob storage and Key Vault -- same shape, no network.
// Written the key ring with one certificate, then re-opened it with another:
//
//   1. keys on disk after first run: 1
//   2. same certificate  -> a session
//   3a. GetAllKeys() returned 1 keys
//   3b. Unprotect threw CryptographicException: Unable to retrieve the decryption key.
//   3c. Protect SUCCEEDED -- so a new key ring was generated over the unreadable one
//   3d. keys on disk now: 2
//
// So the framework's answer to a key it cannot decrypt is not an error. It is a
// warning nobody reads (`DefaultKeyResolver[12]`, "ineligible to be the default
// key because its CreateEncryptor method failed"), a brand-new key, and a site
// that works perfectly for everyone who signs in again. That is #88's own bug
// arriving through the fix for it, which is why the guard is not optional
// hardening.
//
// Note what line 3a says: GetAllKeys() does *not* throw. Key.Descriptor is
// resolved lazily, so the failure only surfaces when something asks the key to do
// work -- which is what CreateEncryptor() is for here.
public class KeyRingCheckTests
{
    private static readonly RecordingLogger<KeyRingPolicy> Silent = new();

    [Fact]
    public void A_ring_whose_keys_all_read_back_is_accepted()
    {
        var logger = new RecordingLogger<KeyRingPolicy>();

        DataProtectionSetup.VerifyKeyRing(new FakeKeyManager(Readable(), Readable()), logger);

        Assert.Equal(2, Assert.Single(logger.Entries).Field("Count"));
    }

    // An empty store is a first run and not a fault: the ring is created the first
    // time something is protected, and refusing here would mean no deployment could
    // ever be the first one.
    [Fact]
    public void An_empty_ring_is_a_first_run_and_is_accepted()
    {
        DataProtectionSetup.VerifyKeyRing(new FakeKeyManager(), Silent);
    }

    [Fact]
    public void One_key_that_cannot_be_decrypted_stops_the_application()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionSetup.VerifyKeyRing(
                new FakeKeyManager(Readable(), Unreadable(), Readable()), Silent));

        Assert.Contains("1 of 3", thrown.Message);

        // The remedy is named, because the cause is two resources away from the
        // process that reports it and "cannot be decrypted" alone sends the reader
        // to the blob rather than to the vault.
        Assert.Contains(DataProtectionSetup.KeyUriKey, thrown.Message);
    }

    // One exception carries one message; a ring with three unreadable keys has
    // three reasons, and the second and third are what say whether this is one
    // deleted key or a vault nobody can reach.
    [Fact]
    public void Every_unreadable_key_is_logged_with_the_reason_it_gave()
    {
        var logger = new RecordingLogger<KeyRingPolicy>();
        var first = Unreadable();
        var second = Unreadable();

        Assert.Throws<InvalidOperationException>(() =>
            DataProtectionSetup.VerifyKeyRing(new FakeKeyManager(first, second), logger));

        var errors = logger.Entries.Where(entry => entry.Level == LogLevel.Error).ToList();

        Assert.Equal(2, errors.Count);
        Assert.Equal(
            new object[] { first.KeyId, second.KeyId },
            errors.Select(entry => entry.Field("KeyId")).ToArray());
    }

    // A revoked key is a deliberate act and is meant to be unusable. Counting it
    // would make revoking one -- the documented answer to a leak -- into an
    // application that will not start.
    [Fact]
    public void A_revoked_key_is_not_evidence_of_anything_being_wrong()
    {
        DataProtectionSetup.VerifyKeyRing(
            new FakeKeyManager(Unreadable(revoked: true), Readable()), Silent);
    }

    // The store being unreachable is the other half of #88's third acceptance
    // test, and it needs no handling here at all: nothing catches it, so it leaves
    // this method the way it arrived and stops the process. The assertion is that
    // it is *not* swallowed -- a `catch` added here while tidying is exactly the
    // change that would turn an outage back into a fresh key ring.
    [Fact]
    public void A_store_that_cannot_be_read_at_all_is_not_swallowed()
    {
        var thrown = Assert.Throws<TimeoutException>(() =>
            DataProtectionSetup.VerifyKeyRing(new UnreachableKeyManager(), Silent));

        Assert.Equal("the key store did not answer", thrown.Message);
    }

    private static FakeKey Readable() => new(Throws: null);

    private static FakeKey Unreadable(bool revoked = false) =>
        new(Throws: new System.Security.Cryptography.CryptographicException(
            "Unable to retrieve the decryption key."),
            IsRevoked: revoked);

    /// <summary>A key that either produces an encryptor or explains why it cannot.</summary>
    // Inherits nothing and is built by hand: IKey is an interface with six members
    // and the one that matters is CreateEncryptor. Descriptor is deliberately a
    // throw rather than a null -- in the real thing it is where the decryption
    // happens, so a guard that reached for it instead would still be caught here.
    private sealed record FakeKey(Exception? Throws, bool IsRevoked = false) : IKey
    {
        public Guid KeyId { get; } = Guid.NewGuid();

        public DateTimeOffset CreationDate => DateTimeOffset.UnixEpoch;

        public DateTimeOffset ActivationDate => DateTimeOffset.UnixEpoch;

        public DateTimeOffset ExpirationDate => DateTimeOffset.MaxValue;

        public IAuthenticatedEncryptorDescriptor Descriptor =>
            throw Throws ?? new InvalidOperationException("nothing here asks for the descriptor");

        public IAuthenticatedEncryptor CreateEncryptor() =>
            Throws is null ? new NoEncryptor() : throw Throws;
    }

    private sealed class NoEncryptor : IAuthenticatedEncryptor
    {
        public byte[] Decrypt(ArraySegment<byte> ciphertext, ArraySegment<byte> additionalAuthenticatedData) => [];

        public byte[] Encrypt(ArraySegment<byte> plaintext, ArraySegment<byte> additionalAuthenticatedData) => [];
    }

    private sealed class FakeKeyManager(params IKey[] keys) : IKeyManager
    {
        public IReadOnlyCollection<IKey> GetAllKeys() => keys;

        public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

        public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) =>
            throw new NotSupportedException("nothing here creates a key");

        public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason = null) =>
            throw new NotSupportedException("nothing here revokes a key");

        public void RevokeKey(Guid keyId, string? reason = null) =>
            throw new NotSupportedException("nothing here revokes a key");
    }

    private sealed class UnreachableKeyManager : IKeyManager
    {
        public IReadOnlyCollection<IKey> GetAllKeys() =>
            throw new TimeoutException("the key store did not answer");

        public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

        public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) =>
            throw new NotSupportedException("nothing here creates a key");

        public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason = null) =>
            throw new NotSupportedException("nothing here revokes a key");

        public void RevokeKey(Guid keyId, string? reason = null) =>
            throw new NotSupportedException("nothing here revokes a key");
    }
}
