using System.Net;

namespace SportsCenter.Services.Email;

// FitZone transactional mails: one card layout (logo, title, greeting, action, note, warning, footer).
// Inline styles + tables only, since mail clients strip <style>, scripts and most modern CSS.
public static class EmailTemplates
{
    public static string Verification(string fullName, string link) => Layout(
        "Xác thực email", fullName, "FitZone gửi bạn link để xác thực email cho tài khoản của bạn.",
        Button(link, "Verify"), "Link có hiệu lực trong 24 giờ.",
        "Nếu bạn không yêu cầu xác thực này, vui lòng bỏ qua email.");

    public static string ResetPassword(string fullName, string link) => Layout(
        "Đặt lại mật khẩu", fullName, "FitZone nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn.",
        Button(link, "Đặt lại mật khẩu"), "Link có hiệu lực trong 15 phút và chỉ dùng được 1 lần.",
        "Nếu bạn không yêu cầu đặt lại mật khẩu, hãy bỏ qua email này. Mật khẩu hiện tại vẫn giữ nguyên.");

    // roleLabel: "hội viên" / "huấn luyện viên" / "lễ tân".
    public static string NewAccount(string fullName, string roleLabel, string email, string password, string loginUrl) => Layout(
        "Tài khoản của bạn đã được tạo", fullName,
        $"Tài khoản {Enc(roleLabel)} FitZone của bạn đã sẵn sàng. Đăng nhập bằng email <b>{Enc(email)}</b> và mật khẩu tạm dưới đây.",
        PasswordBox(password),
        $"Đăng nhập tại <a href=\"{Enc(loginUrl)}\" style=\"color:#4338ca;\">{Enc(loginUrl)}</a>. Bạn sẽ được yêu cầu đổi mật khẩu ở lần đăng nhập đầu tiên.",
        "Không chia sẻ mật khẩu này cho bất kỳ ai, kể cả nhân viên FitZone.");

    private static string Button(string link, string label) =>
        $"""<a href="{Enc(link)}" target="_blank" style="display:inline-block;background-color:#4338ca;color:#ffffff;text-decoration:none;font-size:15px;font-weight:700;padding:14px 48px;border-radius:8px;">{Enc(label)}</a>""";

    // Mail clients run no script, so no copy button: user-select:all makes one tap/click select the
    // whole password on clients that honour it (Apple Mail, iOS); elsewhere it is select-and-copy as usual.
    private static string PasswordBox(string password) => $$"""
        <table role="presentation" cellpadding="0" cellspacing="0" style="border:2px dashed #a5b4fc;border-radius:10px;background-color:#eef2ff;">
          <tr>
            <td style="padding:14px 18px;font-family:'Courier New',Consolas,monospace;font-size:24px;font-weight:700;letter-spacing:3px;color:#1d2433;-webkit-user-select:all;user-select:all;">{{Enc(password)}}</td>
          </tr>
        </table>
        <div style="font-size:12px;color:#8a8fa3;margin-top:8px;">Bôi đen (hoặc nhấn giữ trên điện thoại) để sao chép mật khẩu.</div>
        """;

    private static string Layout(string title, string fullName, string introHtml, string actionHtml, string noteHtml, string warning) => $$"""
        <!doctype html>
        <html lang="vi"><head><meta charset="utf-8"></head>
        <body style="margin:0;padding:0;background-color:#eef0f5;font-family:Arial,Helvetica,sans-serif;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#eef0f5;padding:40px 16px;">
          <tr><td align="center">
            <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px;width:100%;background-color:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 2px 8px rgba(16,24,40,0.08);">
              <tr><td style="padding:32px 32px 8px 32px;" align="center">
                <span style="font-size:22px;font-weight:800;color:#4338ca;letter-spacing:0.5px;">FIT<span style="color:#0b1c30;">ZONE</span></span>
                <div style="font-size:11px;letter-spacing:2px;color:#8a8fa3;margin-top:2px;">SPORTS CENTER</div>
              </td></tr>
              <tr><td style="padding:16px 32px 0 32px;" align="center"><h1 style="margin:0;font-size:20px;color:#1d2433;">{{Enc(title)}}</h1></td></tr>
              <tr><td style="padding:20px 32px 0 32px;color:#3c4257;font-size:14px;line-height:1.6;">
                Chào {{Enc(fullName)}},<br><br>{{introHtml}}
              </td></tr>
              <tr><td style="padding:28px 32px 8px 32px;" align="center">{{actionHtml}}</td></tr>
              <tr><td style="padding:8px 32px 0 32px;" align="center"><span style="font-size:12px;color:#8a8fa3;font-style:italic;">{{noteHtml}}</span></td></tr>
              <tr><td style="padding:24px 32px 32px 32px;">
                <div style="background-color:#fff7e6;border:1px solid #ffe4a3;border-radius:8px;padding:12px 16px;font-size:13px;color:#7a5b00;">{{Enc(warning)}}</div>
              </td></tr>
              <tr><td style="background-color:#0b1c30;padding:18px 24px;" align="center">
                <span style="font-size:11px;color:#aeb4c4;">Đây là email tự động, vui lòng không phản hồi email này.</span><br>
                <span style="font-size:11px;color:#6b7390;">© 2026 FitZone Sports Center</span>
              </td></tr>
            </table>
          </td></tr>
        </table>
        </body></html>
        """;

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
