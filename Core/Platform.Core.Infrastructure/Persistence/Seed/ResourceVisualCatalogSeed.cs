using System.Security.Cryptography;
using System.Text;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.ResourceVisuals;

namespace Platform.Core.Infrastructure.Persistence.Seed;

/// <summary>Original representative drawings; physical proportions describe the pictured area, not tenant inventory.</summary>
public static class ResourceVisualCatalogSeed
{
    public static readonly DateTimeOffset CreatedAt = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    public static Guid StableId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes("resource-visual-v1:" + key)).AsSpan(0, 16));
    public static Dictionary<string, string?> I18n(string pt, string en, string es) => new() { ["pt-BR"] = pt, ["en"] = en, ["es"] = es };

    public static IReadOnlyList<ResourceVisualTag> Tags => new[]
    {
        Tag("sports-courts", "segment", "Esportes/quadras", "Sports/courts", "Deportes/canchas"),
        Tag("commercial-offices", "segment", "Salas comerciais/escritório", "Commercial rooms/offices", "Salas comerciales/oficinas"),
        Tag("events-halls", "segment", "Eventos/salão", "Events/halls", "Eventos/salones"),
        Tag("leisure", "segment", "Lazer", "Leisure", "Ocio"), Tag("health", "segment", "Saúde/consultório", "Health/clinic", "Salud/consultorio"),
        Tag("sand", "floor", "Areia", "Sand", "Arena"), Tag("clay", "floor", "Saibro", "Clay", "Arcilla"),
        Tag("synthetic", "floor", "Sintético", "Synthetic", "Sintético"), Tag("hard", "floor", "Rápida", "Hard", "Dura"),
        Tag("beach-tennis", "sport", "Beach tennis", "Beach tennis", "Tenis playa"), Tag("tennis", "sport", "Tênis", "Tennis", "Tenis"),
        Tag("padel", "sport", "Padel", "Padel", "Pádel"), Tag("footvolley", "sport", "Futevôlei", "Footvolley", "Futvóley"),
        Tag("society", "sport", "Futebol society", "Small-sided football", "Fútbol reducido"), Tag("beach-volleyball", "sport", "Vôlei de areia", "Beach volleyball", "Voleibol de playa"),
    };

    private sealed record Definition(string Slug, string Pt, string En, string Es, string Segment, string[] Attributes, int Width, int Height, string Color, string Icon, string Drawing);
    private static readonly Definition[] Definitions =
    [
        new("beach-tennis", "Beach tennis", "Beach tennis", "Tenis playa", "sports-courts", ["sand", "beach-tennis"], 80, 160, "#DCC08B", "circle-dot", "court"),
        new("tennis-clay", "Tênis saibro", "Clay tennis", "Tenis de arcilla", "sports-courts", ["clay", "tennis"], 1097, 2377, "#B96746", "circle-dot", "tennis"),
        new("tennis-hard", "Tênis rápida", "Hard court tennis", "Tenis de pista dura", "sports-courts", ["hard", "tennis"], 1097, 2377, "#407EAB", "circle-dot", "tennis"),
        new("padel", "Padel", "Padel", "Pádel", "sports-courts", ["synthetic", "padel"], 100, 200, "#38888F", "circle-dot", "padel"),
        new("footvolley", "Futevôlei", "Footvolley", "Futvóley", "sports-courts", ["sand", "footvolley"], 90, 180, "#DCC08B", "circle-dot", "court"),
        new("society", "Society", "Small-sided football", "Fútbol reducido", "sports-courts", ["synthetic", "society"], 250, 450, "#4B9258", "goal", "society"),
        new("beach-volleyball", "Vôlei de areia", "Beach volleyball", "Voleibol de playa", "sports-courts", ["sand", "beach-volleyball"], 80, 160, "#DCC08B", "volleyball", "court"),
        new("commercial-room", "Sala comercial", "Commercial room", "Sala comercial", "commercial-offices", [], 160, 120, "#DDD5C7", "building", "room"),
        new("meeting-room", "Sala de reunião", "Meeting room", "Sala de reuniones", "commercial-offices", [], 180, 120, "#D7DAD3", "users", "meeting"),
        new("consultation-room", "Consultório", "Consultation room", "Consultorio", "health", [], 120, 140, "#D6E6E2", "stethoscope", "clinic"),
        new("events-hall", "Salão de festas", "Events hall", "Salón de fiestas", "events-halls", [], 200, 140, "#E7D6E0", "party-popper", "hall"),
        new("pool", "Piscina", "Pool", "Piscina", "leisure", [], 100, 200, "#50BED3", "waves", "pool"),
        new("barbecue", "Churrasqueira", "Barbecue area", "Zona de barbacoa", "leisure", [], 160, 120, "#DECEB3", "flame", "barbecue"),
    ];

    public static IReadOnlyList<ResourceVisualTemplate> Templates => Definitions.Select((d, n) => new ResourceVisualTemplate(StableId("template:" + d.Slug), CreatedAt)
    {
        Slug = d.Slug, NameI18n = I18n(d.Pt, d.En, d.Es), SortOrder = n,
        DescriptionI18n = I18n($"Ilustração original; área representada {d.Width}:{d.Height}. Proporção representativa, ajustável no mapa.", $"Original illustration; pictured area {d.Width}:{d.Height}. Representative proportion, adjustable in the map.", $"Ilustración original; área representada {d.Width}:{d.Height}. Proporción representativa, ajustable en el mapa."),
    }).ToArray();

    public static IReadOnlyList<ResourceVisualTemplateRevision> Revisions => Definitions.Select(d =>
    {
        var revision = new ResourceVisualTemplateRevision(StableId("revision:" + d.Slug + ":1"), CreatedAt)
        {
            TemplateId = StableId("template:" + d.Slug), RevisionNo = 1, ViewBox = $"0 0 {d.Width} {d.Height}",
            AspectRatio = decimal.Round((decimal)d.Width / d.Height, 6), DefaultColor = d.Color, DefaultIcon = d.Icon,
            FloorSvg = Svg(d, $"<rect x=\"0\" y=\"0\" width=\"{d.Width}\" height=\"{d.Height}\" fill=\"{d.Color}\"/>"),
            LinesSvg = Svg(d, Drawing(d)), NetSvg = d.Drawing is "court" or "tennis" or "padel" ? Svg(d, $"<line x1=\"0\" y1=\"{d.Height / 2}\" x2=\"{d.Width}\" y2=\"{d.Height / 2}\" stroke=\"#383838\" stroke-width=\"{Math.Max(1, d.Width / 100)}\" stroke-dasharray=\"2 2\"/>") : null,
        };
        ResourceVisualArt.PreparePublication(revision, CreatedAt);
        return revision;
    }).ToArray();

    public static IReadOnlyList<ResourceVisualTemplateTag> TemplateTags => Definitions.SelectMany(d => d.Attributes.Prepend(d.Segment).Select(tag => new ResourceVisualTemplateTag { TemplateId = StableId("template:" + d.Slug), TagId = StableId("tag:" + tag) })).ToArray();

    private static ResourceVisualTag Tag(string slug, string facet, string pt, string en, string es) => new(StableId("tag:" + slug), CreatedAt) { Slug = slug, Facet = facet, Kind = facet == "segment" ? ResourceVisualTagKind.Segment : ResourceVisualTagKind.Attribute, NameI18n = I18n(pt, en, es) };
    private static string Svg(Definition d, string content) => $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {d.Width} {d.Height}\">{content}</svg>";
    private static string Drawing(Definition d)
    {
        var w = d.Width; var h = d.Height; var sw = Math.Max(1, w / 100);
        var border = $"<rect x=\"{sw}\" y=\"{sw}\" width=\"{w - 2 * sw}\" height=\"{h - 2 * sw}\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"{sw}\"/>";
        return border + (d.Drawing switch
        {
            "tennis" => $"<path d=\"M {w / 8} 0 V {h} M {w * 7 / 8} 0 V {h} M {w / 8} {h / 4} H {w * 7 / 8} M {w / 8} {h * 3 / 4} H {w * 7 / 8} M {w / 2} {h / 4} V {h * 3 / 4}\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"{sw}\"/>",
            "padel" => $"<path d=\"M 0 {h / 4} H {w} M 0 {h * 3 / 4} H {w} M {w / 2} {h / 4} V {h * 3 / 4}\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"{sw}\"/>",
            "society" => $"<path d=\"M 0 {h / 2} H {w} M {w / 4} 0 V {h / 6} H {w * 3 / 4} V 0 M {w / 4} {h} V {h * 5 / 6} H {w * 3 / 4} V {h}\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"{sw}\"/><circle cx=\"{w / 2}\" cy=\"{h / 2}\" r=\"{w / 6}\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"{sw}\"/>",
            "meeting" => "<rect x=\"40\" y=\"35\" width=\"100\" height=\"50\" rx=\"12\" fill=\"#A68B6B\"/><circle cx=\"60\" cy=\"20\" r=\"9\" fill=\"#596A77\"/><circle cx=\"120\" cy=\"100\" r=\"9\" fill=\"#596A77\"/>",
            "clinic" => "<rect x=\"65\" y=\"30\" width=\"30\" height=\"80\" rx=\"5\" fill=\"#FFFFFF\"/><rect x=\"15\" y=\"20\" width=\"30\" height=\"35\" fill=\"#9CBAAD\"/>",
            "hall" => "<circle cx=\"55\" cy=\"45\" r=\"20\" fill=\"#FFFFFF\"/><circle cx=\"145\" cy=\"45\" r=\"20\" fill=\"#FFFFFF\"/><circle cx=\"100\" cy=\"100\" r=\"20\" fill=\"#FFFFFF\"/>",
            "pool" => "<path d=\"M 15 30 Q 30 20 45 30 T 75 30 M 15 100 Q 30 90 45 100 T 75 100 M 15 170 Q 30 160 45 170 T 75 170\" fill=\"none\" stroke=\"#D5F7FA\" stroke-width=\"3\"/>",
            "barbecue" => "<rect x=\"15\" y=\"15\" width=\"45\" height=\"30\" fill=\"#6F5142\"/><path d=\"M 20 20 H 55 M 20 27 H 55 M 20 34 H 55\" stroke=\"#C4B5A0\" stroke-width=\"2\"/><rect x=\"70\" y=\"60\" width=\"65\" height=\"35\" rx=\"4\" fill=\"#A78B63\"/>",
            "room" => "<rect x=\"20\" y=\"20\" width=\"55\" height=\"30\" fill=\"#B49B79\"/><rect x=\"100\" y=\"75\" width=\"35\" height=\"25\" fill=\"#7E939F\"/>",
            _ => "",
        });
    }
}
