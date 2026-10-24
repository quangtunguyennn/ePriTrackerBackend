using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ePriTrackerBackend.Services.Notification
{
    public interface IEmailService
    {
        Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink);
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

        public async Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink)
        {
            try
            {
                var email = new MimeMessage();
                email.From.Add(new MailboxAddress(_config["SmtpSettings:SenderName"], _config["SmtpSettings:SenderEmail"]));
                email.To.Add(new MailboxAddress(recipientName, recipientEmail));
                email.Subject = $"🔥 [ePriTracker] Cảnh báo giảm giá: {productName}";

                var builder = new BodyBuilder
                {
                    HtmlBody = $@"
                        <div style='font-family: Arial, sans-serif; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            <h2 style='color: #d9534f;'>Xin chào {recipientName},</h2>
                            <p>Sản phẩm bạn đang theo dõi vừa giảm giá chạm mốc kỳ vọng!</p>
                            <hr style='border: none; border-top: 1px solid #eee;' />
                            <p><b>Sản phẩm:</b> {productName}</p>
                            <p><b>Giá hiện tại:</b> <span style='color: #d9534f; font-size: 18px; font-weight: bold;'>{newPrice:N0}đ</span></p>
                            <p><b>Giá mục tiêu của bạn:</b> {targetPrice:N0}đ</p>
                            <br/>
                            <p><a href='{productLink}' style='background-color: #0275d8; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; display: inline-block;'>Xem Sản Phẩm Ngay</a></p>
                        </div>"
                };
                email.Body = builder.ToMessageBody();

                using var smtp = new SmtpClient();
                await smtp.ConnectAsync(_config["SmtpSettings:Server"], int.Parse(_config["SmtpSettings:Port"]!), SecureSocketOptions.StartTls);
                await smtp.AuthenticateAsync(_config["SmtpSettings:Username"], _config["SmtpSettings:Password"]);
                await smtp.SendAsync(email);
                await smtp.DisconnectAsync(true);

                _logger.LogInformation("📧 [EMAIL SUCCESS] Đã gửi email thông báo giá thành công tới {Email}", recipientEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [EMAIL ERROR] Lỗi khi gửi email tới {Email}", recipientEmail);
            }
        }
    }
}