using ePriTrackerBackend.Models.Entities;
using Microsoft.AspNetCore.Identity;

namespace ePriTrackerBackend.Services.Auth
{
    public enum PasswordCheckResult
    {
        Failed,
        Success,
        /// <summary>Đúng mật khẩu nhưng đang lưu dạng cũ (plain text / thuật toán cũ) → nên hash lại và lưu.</summary>
        SuccessRehashNeeded
    }

    public interface IPasswordService
    {
        string Hash(User user, string password);
        PasswordCheckResult Verify(User user, string password);
    }

    /// <summary>
    /// Hash mật khẩu bằng PasswordHasher của ASP.NET Core Identity (PBKDF2).
    /// Vẫn chấp nhận tài khoản cũ lưu plain text để migrate dần khi người dùng đăng nhập.
    /// </summary>
    public class PasswordService : IPasswordService
    {
        private readonly PasswordHasher<User> _hasher = new PasswordHasher<User>();

        public string Hash(User user, string password) => _hasher.HashPassword(user, password);

        public PasswordCheckResult Verify(User user, string password)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user?.Password)) return PasswordCheckResult.Failed;

            try
            {
                var result = _hasher.VerifyHashedPassword(user, user.Password, password);
                if (result == PasswordVerificationResult.Success) return PasswordCheckResult.Success;
                if (result == PasswordVerificationResult.SuccessRehashNeeded) return PasswordCheckResult.SuccessRehashNeeded;
            }
            catch (FormatException) { /* Không phải chuỗi hash (Base64) → mật khẩu cũ dạng plain text */ }

            return user.Password == password ? PasswordCheckResult.SuccessRehashNeeded : PasswordCheckResult.Failed;
        }
    }
}
