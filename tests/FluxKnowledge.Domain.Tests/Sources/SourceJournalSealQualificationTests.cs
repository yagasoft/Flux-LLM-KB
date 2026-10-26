using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceJournalSealQualificationTests
{
    private const string Root = "physical-root";
    private const string EnvironmentId = "tested-environment";
    private static SourceJournalSealQualification Cloud => new(Root, EnvironmentId, "actual-onedrive-hydrated", "eight-cycle-evidence")
        { ProviderName = "OneDrive - Business", ProviderVersion = "26.189.0928.0007" };

    [Fact]
    public void Provider_identity_is_freshly_bound_and_revokes_an_existing_profile_after_upgrade()
    {
        var identity = new WindowsSourceJournalSealQualification.ProviderIdentity(Cloud.ProviderName!, Cloud.ProviderVersion!);
        string? requested = null;
        var qualification = Create(Cloud, path => { requested = path; return identity; });
        var profile = qualification.GetProfile(Root, "owned-root-path");
        Assert.Equal("owned-root-path", requested);
        Assert.Equal(Hash(string.Join('\n', Root, EnvironmentId, Cloud.Scope, Cloud.QualificationId, identity.Name, identity.Version)), profile);
        identity = identity with { Version = "changed-version" };
        Assert.Null(qualification.GetProfile(Root, "owned-root-path"));
        identity = identity with { Name = "different-provider", Version = Cloud.ProviderVersion! };
        Assert.Null(qualification.GetProfile(Root, "owned-root-path"));
    }

    [Fact]
    public void Missing_or_partial_provider_configuration_path_and_lookup_fail_closed()
    {
        Assert.Null(Create(Cloud).GetProfile(Root));
        Assert.Null(Create(Cloud, _ => null).GetProfile(Root, "owned-root-path"));
        foreach (var item in new[] { Cloud with { ProviderName = null }, Cloud with { ProviderVersion = null },
            Cloud with { ProviderName = null, ProviderVersion = null }, Cloud with { ProviderName = " " }, Cloud with { ProviderVersion = " " } })
            Assert.Null(Create(item).GetProfile(Root, "owned-root-path"));
    }

    [Theory]
    [InlineData("local-ntfs-synthetic")]
    [InlineData("isolated-local-ntfs-staging")]
    public void Explicit_local_qualifications_preserve_the_existing_four_field_hash(string scope)
    {
        var item = new SourceJournalSealQualification(Root, EnvironmentId, scope, "local-proof");
        Assert.Equal(Hash(string.Join('\n', Root, EnvironmentId, scope, item.QualificationId)), Create(item).GetProfile(Root));
        Assert.Null(Create(item with { ProviderName = Cloud.ProviderName }).GetProfile(Root, "owned-root-path"));
    }

    [Fact]
    public void Environment_mismatch_still_invalidates_cloud_qualification()
    {
        var options = new SourceJournalOptions { SealQualifications = [Cloud] };
        var qualification = new WindowsSourceJournalSealQualification(options, () => "changed-environment",
            _ => new(Cloud.ProviderName!, Cloud.ProviderVersion!));
        Assert.Null(qualification.GetProfile(Root, "owned-root-path"));
    }

    [Fact]
    public void Native_provider_response_is_bounded_strict_and_independent_of_status()
    {
        var bytes = new byte[1028];
        Encoding.Unicode.GetBytes(Cloud.ProviderName!).CopyTo(bytes, 4);
        Encoding.Unicode.GetBytes(Cloud.ProviderVersion!).CopyTo(bytes, 516);
        var expected = new WindowsSourceJournalSealQualification.ProviderIdentity(Cloud.ProviderName!, Cloud.ProviderVersion!);
        Assert.Equal(expected, WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes, 1028));
        bytes[0] = 1;
        Assert.Equal(expected, WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes, 1028));
        Assert.Null(WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes, 1027));
        Assert.Null(WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes, 1029));
        Assert.Null(WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes[..1027], 1028));
        Array.Fill(bytes, (byte)65, 4, 512); // No UTF-16 terminator.
        Assert.Null(WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes, 1028));
        Array.Clear(bytes, 4, 512);
        Assert.Null(WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes, 1028));
        bytes[4] = 0; bytes[5] = 0xd8; // Unpaired high surrogate.
        Assert.Null(WindowsSourceJournalSealQualification.ParseProviderIdentity(bytes, 1028));
    }

    private static WindowsSourceJournalSealQualification Create(SourceJournalSealQualification item,
        Func<string, WindowsSourceJournalSealQualification.ProviderIdentity?>? provider = null) =>
        new(new() { SealQualifications = [item] }, () => EnvironmentId,
            provider ?? (_ => new(Cloud.ProviderName!, Cloud.ProviderVersion!)));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
