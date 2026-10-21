using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.ResourceVisuals;

public static class ResourceVisualArt
{
    public static void PreparePublication(ResourceVisualTemplateRevision revision, DateTimeOffset now)
    {
        revision.ViewBox = ResourceSvgSanitizer.NormalizeViewBox(revision.ViewBox);
        revision.FloorSvg = ResourceSvgSanitizer.Sanitize(revision.FloorSvg, revision.ViewBox);
        revision.LinesSvg = revision.LinesSvg == null ? null : ResourceSvgSanitizer.Sanitize(revision.LinesSvg, revision.ViewBox);
        revision.NetSvg = revision.NetSvg == null ? null : ResourceSvgSanitizer.Sanitize(revision.NetSvg, revision.ViewBox);
        var box = revision.ViewBox.Split(' ').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (revision.AspectRatio <= 0 || Math.Abs((double)revision.AspectRatio - box[2] / box[3]) > 0.00001) throw new ArgumentException("Aspect ratio must match the SVG viewBox.");
        revision.ArtSha256 = Digest(revision);
        revision.Status = ResourceVisualRevisionStatus.Published;
        revision.PublishedAt = now;
    }

    public static string Digest(ResourceVisualTemplateRevision revision) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        revision.FloorSvg, revision.LinesSvg, revision.NetSvg, revision.ViewBox, revision.AspectRatio,
        revision.DefaultColor, revision.DefaultIcon, revision.SuggestedSlotMinutes, revision.SuggestedCapacityPeople,
    }))));
}
