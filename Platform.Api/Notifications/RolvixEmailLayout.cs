using System.Net;

namespace Platform.Api.Notifications;

/// <summary>
/// Shared HTML chrome for transactional emails (black header/footer, Rolvix. mark).
/// </summary>
public static class RolvixEmailLayout
{
    public static string Wrap(string recipientName, string innerHtmlBody)
    {
        var safeName = WebUtility.HtmlEncode(recipientName);
        return
            $"""
            <!DOCTYPE html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Rolvix</title>
            </head>
            <body style="margin:0;padding:0;background:#f4f4f5;font-family:Arial,Helvetica,sans-serif;color:#18181b;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f4f4f5;padding:24px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border-radius:8px;overflow:hidden;border:1px solid #e4e4e7;">
                      <tr>
                        <td style="background:#000000;padding:16px 24px;">
                          <span style="font-size:20px;font-weight:700;letter-spacing:-0.02em;color:#ffffff;">Rolvix.</span>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:28px 24px;font-size:15px;line-height:1.55;color:#27272a;">
                          <p style="margin:0 0 16px;">Olá {safeName},</p>
                          {innerHtmlBody}
                        </td>
                      </tr>
                      <tr>
                        <td style="background:#000000;padding:14px 24px;font-size:12px;line-height:1.4;color:#ffffff;">
                          <span style="font-weight:700;">Rolvix.</span>
                          <span style="opacity:0.85;"> — Plataforma de gestão operacional</span>
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    public static string InviteBody(
        string inviteUrl,
        string companyName,
        string? portalHost = null)
    {
        var safeUrl = WebUtility.HtmlEncode(inviteUrl);
        var safeCompany = WebUtility.HtmlEncode(companyName);
        var portalLine = string.IsNullOrWhiteSpace(portalHost)
            ? """
              <p style="margin:0 0 12px;">Este acesso é o <strong>painel operacional</strong> em <strong>rolvix.com.br</strong> (ativos, agenda, configurações). O site público do clube (<em>seu-subdominio</em>.rolvix.com.br) é só para sócios/clientes e usa outro login.</p>
              """
            : $"""
              <p style="margin:0 0 12px;">Este acesso é o <strong>painel operacional</strong> em <strong>rolvix.com.br</strong> (ativos, agenda, configurações).</p>
              <p style="margin:0 0 12px;">O site público do clube para sócios/clientes é <strong>{WebUtility.HtmlEncode(portalHost)}</strong> — login diferente (não use a senha de admin lá).</p>
              """;

        return
            $"""
            <p style="margin:0 0 16px;">Você foi convidado(a) para administrar <strong>{safeCompany}</strong> no console Rolvix.</p>
            {portalLine}
            <p style="margin:0 0 20px;">Defina sua senha neste link (válido por 7 dias):</p>
            <p style="margin:0 0 8px;">
              <a href="{safeUrl}" style="display:inline-block;background:#000000;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:6px;font-weight:600;">
                Definir senha
              </a>
            </p>
            <p style="margin:16px 0 0;font-size:12px;color:#71717a;word-break:break-all;">
              Depois, entre em <strong>https://rolvix.com.br/login</strong> com este e-mail.<br /><br />
              Ou copie e cole o link no navegador:<br />{safeUrl}
            </p>
            """;
    }

    public static string RecoveryBody(string resetUrl)
    {
        var safeUrl = WebUtility.HtmlEncode(resetUrl);

        return
            $"""
            <p style="margin:0 0 16px;">Recebemos um pedido para redefinir a senha da sua conta no console Rolvix.</p>
            <p style="margin:0 0 20px;">Clique no botão abaixo para escolher uma nova senha:</p>
            <p style="margin:0 0 8px;">
              <a href="{safeUrl}" style="display:inline-block;background:#000000;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:6px;font-weight:600;">
                Redefinir senha
              </a>
            </p>
            <p style="margin:16px 0 0;font-size:12px;color:#71717a;word-break:break-all;">
              Se você não pediu isso, ignore este e-mail — sua senha permanece a mesma.<br /><br />
              Ou copie e cole o link no navegador:<br />{safeUrl}
            </p>
            """;
    }
}