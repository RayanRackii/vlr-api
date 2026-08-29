using Platform.Api.Modules.Catalog;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogFileRulesTests
{
    [Fact]
    public void Customer_visible_requires_image_magic_and_extension()
    {
        var png = new byte[16];
        png[0] = 0x89;
        png[1] = (byte)'P';
        png[2] = (byte)'N';
        png[3] = (byte)'G';
        CatalogFileRules.Validate(CatalogFileVisibility.CustomerVisible, "a.png", "image/png", png.Length, png);

        Assert.Throws<ArgumentException>(
            () => CatalogFileRules.Validate(
                CatalogFileVisibility.CustomerVisible,
                "a.png",
                "image/png",
                png.Length,
                "not-a-png-file!!"u8));
        Assert.Throws<ArgumentException>(
            () => CatalogFileRules.Validate(
                CatalogFileVisibility.CustomerVisible,
                "a.pdf",
                "application/pdf",
                4,
                "%PDF"u8));
    }

    [Fact]
    public void Internal_files_never_use_public_image_rules()
    {
        CatalogFileRules.Validate(
            CatalogFileVisibility.InternalB2B,
            "part.stl",
            "model/stl",
            128,
            new byte[128]);
        CatalogFileRules.Validate(
            CatalogFileVisibility.InternalB2B,
            "draw.dxf",
            "application/dxf",
            64,
            new byte[64]);
    }
}
