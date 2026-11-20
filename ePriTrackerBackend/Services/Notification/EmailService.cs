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

        public Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink)
            => SendAsync(recipientEmail, recipientName, $"🔥 [ePriTracker] Cảnh báo giảm giá: {productName}", $@"
                        <div style='font-family: Arial, sans-serif; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            <h2 style='color: #d9534f;'>Xin chào {recipientName},</h2>
                            <p>Sản phẩm bạn đang theo dõi vừa giảm giá chạm mốc kỳ vọng!</p>
                            <hr style='border: none; border-top: 1px solid #eee;' />
                            <p><b>Sản phẩm:</b> {productName}</p>
                            <p><b>Giá hiện tại:</b> <span style='color: #d9534f; font-size: 18px; font-weight: bold;'>{newPrice:N0}đ</span></p>
                            <p><b>Giá mục tiêu của bạn:</b> {targetPrice:N0}đ</p>
                            <br/>
                            <p><a href='{productLink}' style='background-color: #0275d8; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; display: inline-block;'>Xem Sản Phẩm Ngay</a></p>
                        </div>", "thông báo giá");

        public Task SendProductDelistedEmailAsync(string recipientEmail, string recipientName, string productName, decimal? lastPrice, string productLink)
        {
            // Tên sản phẩm lấy từ sàn → mã hoá HTML trước khi chèn vào mail
            string name = WebUtility.HtmlEncode(productName ?? "");
            string greeting = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(recipientName) ? "bạn" : recipientName);
            string price = lastPrice > 0 ? $"{lastPrice.Value:N0}đ" : "chưa có";

            return SendAsync(recipientEmail, recipientName, $"🚫 [ePriTracker] Sản phẩm không còn trên sàn: {productName}", $@"
                        <div style='font-family: Arial, sans-serif; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            <h2 style='color: #6c757d;'>Xin chào {greeting},</h2>
                            <p>Sản phẩm bạn đang theo dõi <b>đã bị gỡ khỏi sàn</b> (trang sản phẩm không còn tồn tại sau nhiều lần kiểm tra trong 24 giờ).</p>
                            <hr style='border: none; border-top: 1px solid #eee;' />
                            <p><b>Sản phẩm:</b> {name}</p>
                            <p><b>Giá cuối cùng ghi nhận:</b> {price}</p>
                            <p>ePriTracker đã <b>ngưng cập nhật giá</b> sản phẩm này. Sản phẩm vẫn nằm trong danh sách theo dõi của bạn cho tới khi bạn xoá.
                               Nếu sàn đăng bán lại bằng một link mới, bạn có thể tìm sản phẩm thay thế trong trang chi tiết và theo dõi lại.</p>
                            <p style='color: #999; font-size: 12px;'>Link cũ: {WebUtility.HtmlEncode(productLink ?? "")}</p>
                        </div>", "thông báo sản phẩm bị gỡ");
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
