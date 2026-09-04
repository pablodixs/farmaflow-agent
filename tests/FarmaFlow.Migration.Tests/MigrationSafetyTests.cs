using FarmaFlow.Migration;
using System.Net;
using Xunit;

namespace FarmaFlow.Migration.Tests;

public sealed class MigrationSafetyTests
{
    [Fact]
    public void SystemLabelTemplatesRemainInEveryStorePackage()
    {
        string? predicate = StoreFilter.SeedPredicateForTable(
            "label_templates",
            new HashSet<string>(["store_id", "is_system"], StringComparer.Ordinal));

        Assert.Equal("store_id=@store OR (store_id IS NULL AND is_system)", predicate);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    public void MediaDownloaderBlocksPrivateAndMetadataAddresses(string value)
    {
        Assert.True(MediaArchiver.IsNonPublic(IPAddress.Parse(value)));
    }

    [Fact]
    public void MediaDownloaderAllowsPublicAddress()
    {
        Assert.False(MediaArchiver.IsNonPublic(IPAddress.Parse("8.8.8.8")));
    }

    [Theory]
    [InlineData("text/html", "application/octet-stream")]
    [InlineData("image/svg+xml", "application/octet-stream")]
    [InlineData("image/png; charset=binary", "image/png")]
    public void MediaMimeTypeIsRestrictedToSafeRasterFormats(string value, string expected)
    {
        Assert.Equal(expected, MediaArchiver.NormalizeMimeType(value));
    }

    [Fact]
    public void MediaContentMustMatchASupportedRasterSignature()
    {
        Assert.Equal("image/png", MediaArchiver.DetectImageMimeType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Null(MediaArchiver.DetectImageMimeType("<html>login</html>"u8));
    }
}
