namespace Platform.Api.Notifications;

/// <summary>Inline styles shared by platform email renderers; no external CSS or fonts.</summary>
public static class EmailStyles
{
    public const string Text = "font-size:15px;line-height:1.55;color:#27272a;";
    public const string Button = "display:inline-block;background:#000000;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:6px;font-weight:600;";
    public const string Code = "font-size:28px;line-height:1.2;font-family:Consolas,Monaco,monospace;font-weight:700;letter-spacing:0.12em;color:#18181b;";
    public const string Muted = "font-size:12px;line-height:1.55;color:#71717a;word-break:break-all;";
}
