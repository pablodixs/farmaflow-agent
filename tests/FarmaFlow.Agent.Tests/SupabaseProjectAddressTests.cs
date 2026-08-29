using FarmaFlow.Migration.Core;
using Xunit;

namespace FarmaFlow.Agent.Tests;

public sealed class SupabaseProjectAddressTests
{
    [Fact]
    public void ResolvesDirectDatabaseHost()
    {
        SupabaseProjectAddress result = SupabaseProjectAddress.Resolve(
            "db.abcdefghijklmnopqrst.supabase.co",
            "postgres");

        Assert.Equal("abcdefghijklmnopqrst", result.ProjectRef);
        Assert.Equal("https://abcdefghijklmnopqrst.supabase.co/", result.BaseUri.AbsoluteUri);
    }

    [Fact]
    public void ResolvesSessionPoolerFromUsername()
    {
        SupabaseProjectAddress result = SupabaseProjectAddress.Resolve(
            "aws-0-sa-east-1.pooler.supabase.com",
            "postgres.abcdefghijklmnopqrst");

        Assert.Equal("abcdefghijklmnopqrst", result.ProjectRef);
        Assert.Equal("https://abcdefghijklmnopqrst.supabase.co/", result.BaseUri.AbsoluteUri);
    }

    [Fact]
    public void UsesExplicitProjectUrlWhenPoolerUsernameHasNoRef()
    {
        SupabaseProjectAddress result = SupabaseProjectAddress.Resolve(
            "pooler.example.net",
            "farmaflow",
            "https://abcdefghijklmnopqrst.supabase.co");

        Assert.Equal("abcdefghijklmnopqrst", result.ProjectRef);
    }

    [Theory]
    [InlineData("http://abcdefghijklmnopqrst.supabase.co")]
    [InlineData("https://abcdefghijklmnopqrst.supabase.co/rest/v1")]
    public void RejectsUnsafeOrNonRootProjectUrl(string url)
    {
        Assert.Throws<InvalidOperationException>(() => SupabaseProjectAddress.Resolve(
            "pooler.example.net",
            "farmaflow",
            url));
    }
}
