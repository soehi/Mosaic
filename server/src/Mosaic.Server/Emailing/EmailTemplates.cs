using System.Globalization;
using System.Net;

namespace Mosaic.Server.Emailing;

public static class EmailTemplates
{
    public static EmailMessage Code(string serverName, string code, int ttlSeconds)
    {
        var minutes = Math.Max(1, ttlSeconds / 60);
        var name = WebUtility.HtmlEncode(serverName);
        var text =
            $"{serverName} 邮箱验证码\n\n" +
            $"你的验证码是 {code}，{minutes} 分钟内有效。\n" +
            "请勿向他人转发此验证码；如果这不是你本人的操作，请忽略此邮件。\n";
        var html =
            $"""
            <!doctype html><html lang="zh-CN"><body style="margin:0;padding:32px;background:#f5f6f8;color:#1f2330;font-family:Arial,sans-serif">
            <table role="presentation" style="margin:auto;max-width:520px;width:100%;background:#ffffff;border:1px solid #d9dce3;border-radius:8px">
              <tr><td style="padding:28px">
                <p style="color:#3f6ee0;font-size:14px;margin:0 0 12px">{name}</p>
                <h1 style="font-size:22px;margin:0 0 12px">邮箱验证码</h1>
                <p style="line-height:1.7;margin:0 0 20px">请在 {minutes} 分钟内输入以下验证码：</p>
                <p style="font-size:32px;letter-spacing:8px;font-weight:700;margin:0 0 20px">{WebUtility.HtmlEncode(code)}</p>
                <p style="font-size:12px;color:#6b7280;margin:0">请勿向他人转发此验证码；如果这不是你本人的操作，请忽略此邮件。</p>
              </td></tr>
            </table>
            </body></html>
            """;
        return new EmailMessage(string.Empty, $"{serverName} 邮箱验证码", text, html);
    }

    public static EmailMessage Test(string serverName)
    {
        var text = $"{serverName} 邮件服务测试\n\n邮件服务已成功连接。\n";
        var html =
            $"""
            <!doctype html><html lang="zh-CN"><body style="margin:0;padding:32px;background:#f5f6f8;color:#1f2330;font-family:Arial,sans-serif">
            <p>{WebUtility.HtmlEncode(serverName)} 邮件服务已成功连接。</p>
            </body></html>
            """;
        return new EmailMessage(string.Empty, $"{serverName} 邮件服务测试", text, html);
    }
}
