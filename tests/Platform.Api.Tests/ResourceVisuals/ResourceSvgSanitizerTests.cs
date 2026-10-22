using System.Text;
using System.Xml.Linq;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence.Seed;
using Platform.Core.Infrastructure.ResourceVisuals;

namespace Platform.Api.Tests.ResourceVisuals;

public sealed class ResourceSvgSanitizerTests
{
    private static string Svg(string body, string attributes = "") => $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 200\" {attributes}>{body}</svg>";
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<foreignObject><div>unsafe</div></foreignObject>")]
    [InlineData("<image href=\"https://evil.test/a.svg\"/>")]
    [InlineData("<use href=\"#target\"/>")]
    [InlineData("<rect onclick=\"alert(1)\"/>")]
    [InlineData("<rect style=\"fill:url(https://evil.test)\"/>")]
    [InlineData("<animate attributeName=\"x\"/>")]
    [InlineData("<rect fill=\"url(https://evil.test/a)\"/>")]
    [InlineData("<rect fill=\"url(#missing)\"/>")]
    [InlineData("<rect fill=\"javascript:alert(1)\"/>")]
    [InlineData("<rect width=\"NaN\"/>")]
    [InlineData("<rect width=\"1e309\"/>")]
    [InlineData("<rect width=\"-1\"/>")]
    [InlineData("<rect id=\"same\"/><rect id=\"same\"/>")]
    [InlineData("<defs><pattern id=\"p\"><rect fill=\"url(#p)\"/></pattern></defs>")]
    [InlineData("<?xml-stylesheet href=\"https://evil.test/a.css\"?>")]
    public void Rejects_hostile_structure(string body) => Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.Sanitize(Svg(body), "0 0 100 200"));

    [Fact]
    public void Rejects_external_entities_and_dtd() => Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.Sanitize("<!DOCTYPE svg [<!ENTITY ex SYSTEM 'file:///etc/passwd'>]>" + Svg("<g>&ex;</g>"), "0 0 100 200"));
    [Fact]
    public void Rejects_mismatched_or_infinite_viewbox()
    {
        Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.Sanitize(Svg(""), "0 0 200 100"));
        Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.NormalizeViewBox("0 0 Infinity 100"));
        Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.NormalizeViewBox("0 0 0 100"));
    }
    [Fact]
    public void Enforces_size_elements_and_depth()
    {
        Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.Sanitize(Svg(new string(' ', ResourceSvgSanitizer.MaxLayerBytes)), "0 0 100 200"));
        Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.Sanitize(Svg(string.Concat(Enumerable.Repeat("<rect/>", 2000))), "0 0 100 200"));
        Assert.Throws<ArgumentException>(() => ResourceSvgSanitizer.Sanitize(Svg(string.Concat(Enumerable.Repeat("<g>", 33)) + string.Concat(Enumerable.Repeat("</g>", 33))), "0 0 100 200"));
    }
    [Fact]
    public void Accepts_limited_vector_pattern_and_prefixes_layer_references()
    {
        var pattern = Svg("<defs><pattern id=\"sand\" width=\"4\" height=\"4\" patternUnits=\"userSpaceOnUse\"><circle cx=\"1\" cy=\"1\" r=\"1\" fill=\"#D0B080\"/></pattern></defs><rect width=\"100\" height=\"200\" fill=\"url(#sand)\"/>");
        var art = ResourceSvgSanitizer.Compose("0 0 100 200", pattern, pattern, null);
        Assert.Contains("id=\"floor-sand\"", art); Assert.Contains("url(#floor-sand)", art);
        Assert.Contains("id=\"lines-sand\"", art); Assert.Contains("url(#lines-sand)", art);
        Assert.Equal(2, XDocument.Parse(art).Root!.Elements().Count());
    }
    [Fact]
    public void Composition_preserves_root_presentation_attributes()
    {
        var art = ResourceSvgSanitizer.Compose("0 0 100 200", Svg("<rect width=\"50\" height=\"50\"/>", "fill=\"#ABCDEF\" opacity=\"0.5\""), null, null);
        var group = XDocument.Parse(art).Root!.Elements().Single();
        Assert.Equal("#ABCDEF", group.Attribute("fill")!.Value); Assert.Equal("0.5", group.Attribute("opacity")!.Value);
    }
    [Fact]
    public void Starter_catalog_has_thirteen_original_safe_translated_immutable_art_revisions()
    {
        Assert.Equal(13, ResourceVisualCatalogSeed.Templates.Count);
        var first = ResourceVisualCatalogSeed.Revisions;
        var second = ResourceVisualCatalogSeed.Revisions;
        Assert.Equal(first.Select(x => x.Id), second.Select(x => x.Id));
        Assert.Equal(first.Select(x => x.ArtSha256), second.Select(x => x.ArtSha256));
        Assert.Equal(13, first.Select(x => x.ArtSha256).Distinct().Count());
        foreach (var revision in first)
        {
            Assert.Equal(ResourceVisualRevisionStatus.Published, revision.Status); Assert.Equal(64, revision.ArtSha256.Length);
            Assert.Equal(60, revision.SuggestedSlotMinutes); Assert.Null(revision.SuggestedCapacityPeople);
            Assert.Equal(revision.ArtSha256, ResourceVisualArt.Digest(revision));
            Assert.NotEmpty(ResourceSvgSanitizer.Compose(revision.ViewBox, revision.FloorSvg, revision.LinesSvg, revision.NetSvg));
        }
        Assert.All(ResourceVisualCatalogSeed.Templates, t => Assert.Equal(["en", "es", "pt-BR"], t.NameI18n.Keys.Order().ToArray()));
    }
}
