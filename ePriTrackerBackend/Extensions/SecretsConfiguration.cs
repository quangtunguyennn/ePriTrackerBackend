using System.Text;

namespace ePriTrackerBackend.Extensions
{
    /// <summary>
    /// Đọc các giá trị bí mật. KHÔNG để trong appsettings.json (file này nằm trên GitHub):
    /// - Máy dev: User Secrets – <c>dotnet user-secrets set "JwtSettings:SecretKey" "..."</c> (chạy trong thư mục project).
    /// - Server: biến môi trường, vd <c>JwtSettings__SecretKey</c>, <c>YouTubeApiKey</c>, <c>SmtpSettings__Password</c>.
    /// Thiếu JWT key → không khởi động được (báo lỗi rõ ràng); thiếu YouTube / SMTP → tính năng đó tắt, phần còn lại vẫn chạy.
    /// </summary>
    public static class SecretsConfiguration
    {
        public const string JwtSecretKey = "JwtSettings:SecretKey";
        public const string YouTubeApiKey = "YouTubeApiKey";
        public const string SmtpPassword = "SmtpSettings:Password";

        /// <summary>HMAC-SHA256 cần khoá ít nhất 256 bit.</summary>
        public const int MinJwtKeyBytes = 32;

        public const int DefaultTokenLifetimeMinutes = 60;

        /// <exception cref="InvalidOperationException">Chưa cấu hình hoặc khoá quá ngắn.</exception>
        public static byte[] GetJwtSigningKey(IConfiguration configuration)
        {
            string key = configuration?[JwtSecretKey];
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < MinJwtKeyBytes)
            {
                throw new InvalidOperationException(
                    $"Chưa cấu hình \"{JwtSecretKey}\" (cần ít nhất {MinJwtKeyBytes} byte). " +
                    "Máy dev: chạy trong thư mục ePriTrackerBackend: dotnet user-secrets set \"JwtSettings:SecretKey\" \"<chuỗi ngẫu nhiên dài>\". " +
                    "Server: đặt biến môi trường JwtSettings__SecretKey.");
            }
            return Encoding.UTF8.GetBytes(key);
        }

        /// <summary>"JwtSettings:DurationInMinutes", giới hạn 5 phút – 1 ngày; thiếu / sai → 60 phút.</summary>
        public static int GetTokenLifetimeMinutes(IConfiguration configuration)
        {
            if (!int.TryParse(configuration?["JwtSettings:DurationInMinutes"], out int minutes) || minutes <= 0)
                return DefaultTokenLifetimeMinutes;
            return Math.Clamp(minutes, 5, 1440);
        }

        public static bool HasValue(IConfiguration configuration, string key)
            => !string.IsNullOrWhiteSpace(configuration?[key]);
    }
}
