using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;

namespace ePriTrackerBackend.Services.Auth
{
    public interface IAuthService
    {
        /// <returns>null nếu sai email hoặc mật khẩu.</returns>
        Task<LoginResultDTO> LoginAsync(string email, string password);

        /// <returns>false nếu email đã được dùng.</returns>
        Task<bool> RegisterAsync(registerRequestDTO request);

        /// <returns>null nếu không tìm thấy user.</returns>
        Task<CurrentUserDTO> GetCurrentUserAsync(string email);
    }

    public class AuthService : IAuthService
    {
        private const string DefaultRoleName = "User";

        private readonly IUserRepository _users;
        private readonly IPasswordService _passwords;
        private readonly IJwtTokenService _tokens;
        private readonly IUnitOfWork _unitOfWork;

        public AuthService(IUserRepository users, IPasswordService passwords, IJwtTokenService tokens, IUnitOfWork unitOfWork)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _passwords = passwords ?? throw new ArgumentNullException(nameof(passwords));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        }

        public async Task<LoginResultDTO> LoginAsync(string email, string password)
        {
            var user = await _users.FindByEmailAsync(email, asTracking: true);
            if (user == null) return null;

            var check = _passwords.Verify(user, password);
            if (check == PasswordCheckResult.Failed) return null;

            if (check == PasswordCheckResult.SuccessRehashNeeded)
            {
                // Tài khoản cũ còn lưu plain text (hoặc hash đời cũ) → hash lại và lưu (migrate dần)
                user.Password = _passwords.Hash(user, password);
                await _unitOfWork.SaveChangesAsync();
            }

            var roles = await _users.GetRoleNamesAsync(user.UserId);

            return new LoginResultDTO
            {
                Token = _tokens.CreateToken(user, roles),
                Roles = roles
            };
        }

        public async Task<bool> RegisterAsync(registerRequestDTO request)
        {
            if (await _users.EmailExistsAsync(request.Email)) return false;

            // 1. Tạo User mới
            var newUser = new User()
            {
                UserId = Guid.NewGuid(),
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                Password = string.Empty, // gán hash ngay bên dưới
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            newUser.Password = _passwords.Hash(newUser, request.Password);

            await _users.AddAsync(newUser);

            // 2. Gán mặc định Role "User" cho tài khoản mới đăng ký
            var defaultRole = await _users.FindRoleByNameAsync(DefaultRoleName);
            if (defaultRole != null)
            {
                await _users.AddUserRoleAsync(new UserRole()
                {
                    Id = Guid.NewGuid(),
                    UserId = newUser.UserId,
                    RoleId = defaultRole.RoleId
                });
            }

            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        public async Task<CurrentUserDTO> GetCurrentUserAsync(string email)
        {
            var user = await _users.FindByEmailAsync(email);
            if (user == null) return null;

            return new CurrentUserDTO
            {
                UserName = $"{user.FirstName} {user.LastName}",
                UserEmail = user.Email,
                Roles = await _users.GetRoleNamesAsync(user.UserId)
            };
        }
    }
}
