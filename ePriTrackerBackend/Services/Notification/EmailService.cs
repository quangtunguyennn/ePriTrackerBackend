using System.Globalization;
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

        // Email SONG NGỮ (tiếng Anh trên, tiếng Việt dưới): job gửi mail chạy nền, không biết người dùng đang chọn ngôn ngữ nào
        // trên web → gửi cả hai, không cần lưu ngôn ngữ vào DB. Tên sản phẩm lấy từ sàn → mã hoá HTML trước khi chèn.
        public Task SendPriceDropEmailAsync(string recipientEmail, string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink)
        {
            var (subject, html) = BuildPriceDropEmail(recipientName, productName, newPrice, targetPrice, productLink);
            return SendAsync(recipientEmail, recipientName, subject, html, "price alert");
        }

        public Task SendProductDelistedEmailAsync(string recipientEmail, string recipientName, string productName, decimal? lastPrice, string productLink)
        {
            var (subject, html) = BuildDelistedEmail(recipientName, productName, lastPrice, productLink);
            return SendAsync(recipientEmail, recipientName, subject, html, "delisted product");
        }

        /// <summary>Tiêu đề + nội dung email đạt giá mục tiêu (song ngữ). Tách riêng để test không cần SMTP.</summary>
        public static (string Subject, string Html) BuildPriceDropEmail(string recipientName, string productName, decimal newPrice, decimal targetPrice, string productLink)
        {
            string name = WebUtility.HtmlEncode(productName ?? "");
            string link = WebUtility.HtmlEncode(productLink ?? "");
            string button = $"<p><a href='{link}' style='background-color: #ea580c; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; display: inline-block;'>View product / Xem sản phẩm</a></p>";

            string english = $@"
                            <h2 style='color: #c2410c;'>Hi {Greeting(recipientName, "there")},</h2>
                            <p>A product you're tracking just dropped to your target price.</p>
                            <p><b>Product:</b> {name}</p>
                            <p><b>Current price:</b> <span style='color: #c2410c; font-size: 18px; font-weight: bold;'>{NotificationService.FormatPrice(newPrice)}</span></p>
                            <p><b>Your target:</b> {NotificationService.FormatPrice(targetPrice)}</p>";
            string vietnamese = $@"
                            <h2 style='color: #c2410c;'>Xin chào {Greeting(recipientName, "bạn")},</h2>
                            <p>Một sản phẩm bạn đang theo dõi vừa giảm đến giá mục tiêu.</p>
                            <p><b>Sản phẩm:</b> {name}</p>
                            <p><b>Giá hiện tại:</b> <span style='color: #c2410c; font-size: 18px; font-weight: bold;'>{FormatPriceVi(newPrice)}</span></p>
                            <p><b>Giá mục tiêu của bạn:</b> {FormatPriceVi(targetPrice)}</p>";

            return ($"[ePriTracker] Price target reached / Đã chạm giá mục tiêu: {productName}", Layout(english, vietnamese, button));
        }

        /// <summary>Tiêu đề + nội dung email sản phẩm bị gỡ khỏi sàn (song ngữ).</summary>
        public static (string Subject, string Html) BuildDelistedEmail(string recipientName, string productName, decimal? lastPrice, string productLink)
        {
            string name = WebUtility.HtmlEncode(productName ?? "");
            string priceEn = lastPrice > 0 ? NotificationService.FormatPrice(lastPrice.Value) : "not available";
            string priceVi = lastPrice > 0 ? FormatPriceVi(lastPrice.Value) : "không có";
            string footer = $"<p style='color: #999; font-size: 12px;'>Old link / Link cũ: {WebUtility.HtmlEncode(productLink ?? "")}</p>";

            string english = $@"
                            <h2 style='color: #475569;'>Hi {Greeting(recipientName, "there")},</h2>
                            <p>A product you're tracking <b>was removed from the platform</b> (its page no longer exists after several checks over 24 hours).</p>
                            <p><b>Product:</b> {name}</p>
                            <p><b>Last recorded price:</b> {priceEn}</p>
                            <p>ePriTracker has <b>stopped updating its price</b>. It stays in your list until you remove it.
                               If the platform lists it again under a new link, you can find it among the suggestions on the product page and track it again.</p>";
            string vietnamese = $@"
                            <h2 style='color: #475569;'>Xin chào {Greeting(recipientName, "bạn")},</h2>
                            <p>Một sản phẩm bạn đang theo dõi <b>đã bị gỡ khỏi sàn</b> (trang sản phẩm không còn tồn tại sau nhiều lần kiểm tra trong 24 giờ).</p>
                            <p><b>Sản phẩm:</b> {name}</p>
                            <p><b>Giá cuối cùng ghi nhận:</b> {priceVi}</p>
                            <p>ePriTracker đã <b>ngưng cập nhật giá</b> sản phẩm này. Sản phẩm vẫn nằm trong danh sách cho tới khi bạn xoá.
                               Nếu sàn đăng lại với link mới, bạn có thể tìm thấy nó trong phần gợi ý ở trang sản phẩm và theo dõi lại.</p>";

            return ($"[ePriTracker] Product no longer available / Sản phẩm không còn trên sàn: {productName}", Layout(english, vietnamese, footer));
        }

        private static string Greeting(string recipientName, string fallback)
            => WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(recipientName) ? fallback : recipientName);

        /// <summary>"1.500.000 ₫" – cách viết giá quen thuộc với người Việt (phần tiếng Anh dùng "1,500,000 ₫").</summary>
        public static string FormatPriceVi(decimal price)
            => $"{price.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))} ₫";

        private static string Layout(string english, string vietnamese, string after) => $@"
                        <div style='font-family: Arial, sans-serif; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            {english}
                            <hr style='border: none; border-top: 1px solid #eee; margin: 20px 0;' />
                            <div lang='vi'>{vietnamese}</div>
                            <br/>
                            {after}
                        </div>";

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
