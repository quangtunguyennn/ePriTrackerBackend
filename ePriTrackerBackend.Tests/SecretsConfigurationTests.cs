using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using ePriTrackerBackend.Extensions;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services.Auth;
using ePriTrackerBackend.Services.Notification;
using ePriTrackerBackend.Services.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests
{
    public class SecretsConfigurationTests
    {
        private const string ValidKey = "test-secret-key-phai-du-dai-it-nhat-32-byte!!";

        private static IConfiguration Config(params (string Key, string? Value)[] values)
            => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("qua-ngan-chi-20-byte")]
        public void MissingOrShortJwtKey_Throws_WithHowToFix(string? key)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SecretsConfiguration.GetJwtSigningKey(Config(("JwtSettings:SecretKey", key))));
            Assert.Contains("dotnet user-secrets set", ex.Message);
            Assert.Contains("JwtSettings__SecretKey", ex.Message);
        }

        [Fact]
        public void ValidJwtKey_ReturnsBytes()
        {
            Assert.Equal(ValidKey.Length, SecretsConfiguration.GetJwtSigningKey(Config(("JwtSettings:SecretKey", ValidKey))).Length);
        }

        [Theory]
        [InlineData(null, 60)]
        [InlineData("abc", 60)]
        [InlineData("0", 60)]
        [InlineData("120", 120)]
        [InlineData("1", 5)]
        [InlineData("99999", 1440)]
        public void TokenLifetime_FromConfig_Clamped(string? value, int expected)
        {
            Assert.Equal(expected, SecretsConfiguration.GetTokenLifetimeMinutes(Config(("JwtSettings:DurationInMinutes", value))));
        }

        [Fact]
        public void JwtToken_UsesConfiguredLifetime()
        {
            var service = new JwtTokenService(Config(
                ("JwtSettings:SecretKey", ValidKey), ("JwtSettings:Issuer", "i"), ("JwtSettings:Audience", "a"), ("JwtSettings:DurationInMinutes", "120")));

            var token = new JwtSecurityTokenHandler().ReadJwtToken(service.CreateToken(new User { UserId = Guid.NewGuid(), Email = "u@test.vn" }, new[] { "User" }));

            Assert.InRange(token.ValidTo - DateTime.UtcNow, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        }

        [Fact]
        public void JwtTokenService_WithoutKey_FailsFast()
        {
            Assert.Throws<InvalidOperationException>(() => new JwtTokenService(Config()));
        }

        [Fact]
        public async Task YouTube_WithoutKey_ReturnsNoVideos_InsteadOfCrashing()
        {
            var videos = await new YouTubeService(Config(), NullLogger<YouTubeService>.Instance).GetReviewVideosAsync("iPhone 17");
            Assert.Empty(videos);
        }

        [Fact]
        public async Task Email_WithoutPassword_IsSkipped()
        {
            // Không có mật khẩu → không mở kết nối SMTP (server "smtp.invalid" sẽ lỗi nếu bị gọi), không ném lỗi
            var service = new EmailService(Config(("SmtpSettings:Server", "smtp.invalid"), ("SmtpSettings:Port", "587")), NullLogger<EmailService>.Instance);
            await service.SendProductDelistedEmailAsync("u@test.vn", "U", "SP", 1000, "https://x");
        }

        private sealed class Env : Microsoft.Extensions.Hosting.IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Development";
            public string ApplicationName { get; set; } = "ePriTrackerBackend";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        }

        private static string TempSecretsPath() => Path.Combine(Path.GetTempPath(), "epri-secrets-" + Guid.NewGuid().ToString("N"), "secrets.json");

        [Fact]
        public void DevWithoutJwtKey_GeneratesAndSaves_KeepingOtherSecrets()
        {
            string path = TempSecretsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "﻿{ \"YouTubeApiKey\": \"yt\", \"JwtSettings\": { \"SecretKey\": \"cu\" } }");
            try
            {
                var config = new ConfigurationManager();
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:SecretKey"] = "" });

                Assert.True(SecretsConfiguration.EnsureDevelopmentJwtKey(config, new Env(), path));

                byte[] key = SecretsConfiguration.GetJwtSigningKey(config); // dùng được ngay, không ném lỗi
                var saved = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
                Assert.Equal(System.Text.Encoding.UTF8.GetString(key), saved.GetProperty("JwtSettings:SecretKey").GetString());
                Assert.Equal("yt", saved.GetProperty("YouTubeApiKey").GetString());
                Assert.False(saved.GetProperty("JwtSettings").TryGetProperty("SecretKey", out _)); // khoá cũ dạng lồng nhau bị bỏ
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        [Fact]
        public void DevWithoutSecretsFile_CreatesIt()
        {
            string path = TempSecretsPath();
            try
            {
                var config = new ConfigurationManager();
                Assert.True(SecretsConfiguration.EnsureDevelopmentJwtKey(config, new Env(), path));
                Assert.True(File.Exists(path));
                Assert.NotEmpty(SecretsConfiguration.GetJwtSigningKey(config));
            }
            finally { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        [Fact]
        public void ValidKeyAlready_NothingChanges()
        {
            string path = TempSecretsPath();
            var config = new ConfigurationManager();
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:SecretKey"] = ValidKey });

            Assert.False(SecretsConfiguration.EnsureDevelopmentJwtKey(config, new Env(), path));
            Assert.Equal(ValidKey, config["JwtSettings:SecretKey"]);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Production_NeverGenerates()
        {
            // Server phải đặt khoá thật bằng biến môi trường – không tự sinh
            string path = TempSecretsPath();
            var config = new ConfigurationManager();

            Assert.False(SecretsConfiguration.EnsureDevelopmentJwtKey(config, new Env { EnvironmentName = "Production" }, path));
            Assert.Throws<InvalidOperationException>(() => SecretsConfiguration.GetJwtSigningKey(config));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void BrokenSecretsFile_NotOverwritten_TemporaryKeyUsed()
        {
            string path = TempSecretsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ hỏng");
            try
            {
                var config = new ConfigurationManager();
                Assert.True(SecretsConfiguration.EnsureDevelopmentJwtKey(config, new Env(), path));
                Assert.NotEmpty(SecretsConfiguration.GetJwtSigningKey(config));
                Assert.Equal("{ hỏng", File.ReadAllText(path));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        [Fact]
        public void CommittedAppSettings_ContainNoSecrets()
        {
            // appsettings.json nằm trên GitHub: bí mật chỉ được đặt bằng User Secrets / biến môi trường
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ePriTrackerBackend", "appsettings.json"));
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;

            Assert.Equal("", root.GetProperty("JwtSettings").GetProperty("SecretKey").GetString());
            Assert.Equal("", root.GetProperty("YouTubeApiKey").GetString());
            Assert.Equal("", root.GetProperty("SmtpSettings").GetProperty("Password").GetString());
        }
    }
}
