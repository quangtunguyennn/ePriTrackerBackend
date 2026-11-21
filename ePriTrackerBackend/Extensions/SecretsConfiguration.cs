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

        /// <summary>Đường dẫn secrets.json (User Secrets) của assembly chứa <typeparamref name="TMarker"/>; null nếu không khai báo UserSecretsId.</summary>
        public static string UserSecretsPath<TMarker>()
        {
            var attribute = typeof(TMarker).Assembly
                .GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute), false)
                .OfType<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>().FirstOrDefault();
            if (attribute == null) return null;
            try { return Microsoft.Extensions.Configuration.UserSecrets.PathHelper.GetSecretsPathFromSecretsId(attribute.UserSecretsId); }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>
        /// Máy dev chưa có khoá JWT (hoặc khoá quá ngắn) → tự sinh 64 byte ngẫu nhiên, lưu vào User Secrets (giữ nguyên các khoá khác)
        /// để lần sau dùng lại, và dùng ngay cho lần chạy này. Chỉ khi Development – server vẫn bắt buộc đặt biến môi trường.
        /// </summary>
        /// <returns>true nếu vừa sinh khoá mới.</returns>
        public static bool EnsureDevelopmentJwtKey(IConfigurationManager configuration, IHostEnvironment environment, string secretsFilePath)
        {
            if (configuration == null || environment == null || !environment.IsDevelopment()) return false;
            string current = configuration[JwtSecretKey];
            if (!string.IsNullOrWhiteSpace(current) && Encoding.UTF8.GetByteCount(current) >= MinJwtKeyBytes) return false;

            string key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
            if (!string.IsNullOrEmpty(secretsFilePath))
            {
                try
                {
                    SaveToSecretsFile(secretsFilePath, JwtSecretKey, key);
                    Console.WriteLine($"[Secrets] Đã tự tạo JwtSettings:SecretKey cho máy dev và lưu vào User Secrets ({secretsFilePath}).");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.Json.JsonException)
                {
                    // Không ghi được (vd secrets.json đang sai cú pháp – không ghi đè để khỏi mất khoá khác) → khoá chỉ dùng cho lần chạy này
                    Console.WriteLine($"[Secrets] Không lưu được khoá JWT vào User Secrets ({ex.Message}) – dùng khoá tạm, khởi động lại sẽ phải đăng nhập lại.");
                }
            }
            configuration.AddInMemoryCollection(new Dictionary<string, string> { [JwtSecretKey] = key });
            return true;
        }

        /// <summary>Ghi một khoá vào secrets.json theo đúng định dạng của "dotnet user-secrets" (khoá phẳng "A:B"), giữ các khoá khác.</summary>
        private static void SaveToSecretsFile(string path, string name, string value)
        {
            var root = new System.Text.Json.Nodes.JsonObject();
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                    root = System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions
                    {
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    }) as System.Text.Json.Nodes.JsonObject
                        ?? throw new System.Text.Json.JsonException("secrets.json không phải object JSON");
            }

            // Xoá dạng lồng nhau cũ (nếu có) để chỉ còn một nơi giữ giá trị
            int colon = name.IndexOf(':');
            if (colon > 0 && root[name.Substring(0, colon)] is System.Text.Json.Nodes.JsonObject section)
                section.Remove(name.Substring(colon + 1));
            root[name] = value;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
