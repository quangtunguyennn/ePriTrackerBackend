using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ePriTrackerBackend.Services.Notification
{
    public interface IEmailService
    {
        Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink);

        /// <summary>Sản phẩm đang theo dõi đã bị gỡ khỏi sàn – hệ thống ngưng cào giá.</summary>
        Task SendProductDelistedEmailAsync(string recipientEmail, string recipientName, string productName, decimal? lastPrice, string productLink);
    }

    public class EmailService : IEmailService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration config, ILogger<EmailService> logger)
        {
            _config = config;
            _logger = logger;
        }

        // Email tiếng Anh (ngôn ngữ chính của hệ thống). Tên sản phẩm lấy từ sàn → mã hoá HTML trước khi chèn.
        public Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink)
        {
            string name = WebUtility.HtmlEncode(productName ?? "");
            string greeting = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(recipientName) ? "there" : recipientName);
            string link = WebUtility.HtmlEncode(productLink ?? "");

            return SendAsync(recipientEmail, recipientName, $"[ePriTracker] Price target reached: {productName}", $@"
                        <div style='font-family: Arial, sans-serif; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            <h2 style='color: #c2410c;'>Hi {greeting},</h2>
                            <p>A product you're tracking just dropped to your target price.</p>
                            <hr style='border: none; border-top: 1px solid #eee;' />
                            <p><b>Product:</b> {name}</p>
                            <p><b>Current price:</b> <span style='color: #c2410c; font-size: 18px; font-weight: bold;'>{NotificationService.FormatPrice(newPrice)}</span></p>
                            <p><b>Your target:</b> {NotificationService.FormatPrice(targetPrice)}</p>
                            <br/>
                            <p><a href='{link}' style='background-color: #ea580c; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; display: inline-block;'>View product</a></p>
                        </div>", "price alert");
        }

        public Task SendProductDelistedEmailAsync(string recipientEmail, string recipientName, string productName, decimal? lastPrice, string productLink)
        {
            string name = WebUtility.HtmlEncode(productName ?? "");
            string greeting = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(recipientName) ? "there" : recipientName);
            string price = lastPrice > 0 ? NotificationService.FormatPrice(lastPrice.Value) : "not available";

            return SendAsync(recipientEmail, recipientName, $"[ePriTracker] Product no longer available: {productName}", $@"
                        <div style='font-family: Arial, sans-serif; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            <h2 style='color: #475569;'>Hi {greeting},</h2>
                            <p>A product you're tracking <b>was removed from the platform</b> (its page no longer exists after several checks over 24 hours).</p>
                            <hr style='border: none; border-top: 1px solid #eee;' />
                            <p><b>Product:</b> {name}</p>
                            <p><b>Last recorded price:</b> {price}</p>
                            <p>ePriTracker has <b>stopped updating its price</b>. It stays in your list until you remove it.
                               If the platform lists it again under a new link, you can find it among the suggestions on the product page and track it again.</p>
                            <p style='color: #999; font-size: 12px;'>Old link: {WebUtility.HtmlEncode(productLink ?? "")}</p>
                        </div>", "delisted product");
        }

        private async Task SendAsync(string recipientEmail, string recipientName, string subject, string htmlBody, string kind)
        {
            // Mật khẩu SMTP lấy từ User Secrets / biến môi trường "SmtpSettings__Password"; thiếu → bỏ qua gửi mail (thông báo trong DB vẫn được ghi)
            if (string.IsNullOrWhiteSpace(_config[ePriTrackerBackend.Extensions.SecretsConfiguration.SmtpPassword]))
            {
                _logger.LogWarning("📧 [EMAIL SKIPPED] Chưa cấu hình SmtpSettings:Password – không gửi email {Kind} tới {Email}", kind, recipientEmail);
                return;
            }

            try
            {
                var email = new MimeMessage();
                email.From.Add(new MailboxAddress(_config["SmtpSettings:SenderName"], _config["SmtpSettings:SenderEmail"]));
                email.To.Add(new MailboxAddress(recipientName, recipientEmail));
                email.Subject = subject;
                email.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

                using var smtp = new SmtpClient();
                await smtp.ConnectAsync(_config["SmtpSettings:Server"], int.Parse(_config["SmtpSettings:Port"]!), SecureSocketOptions.StartTls);
                await smtp.AuthenticateAsync(_config["SmtpSettings:Username"], _config["SmtpSettings:Password"]);
                await smtp.SendAsync(email);
                await smtp.DisconnectAsync(true);

                _logger.LogInformation("📧 [EMAIL SUCCESS] Đã gửi email {Kind} thành công tới {Email}", kind, recipientEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [EMAIL ERROR] Lỗi khi gửi email {Kind} tới {Email}", kind, recipientEmail);
            }
        }
    }
}
