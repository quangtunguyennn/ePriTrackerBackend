using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Repositories
{
    public interface IUserRepository
    {
        /// <param name="asTracking">true khi cần sửa user rồi lưu (ví dụ hash lại mật khẩu).</param>
        Task<User> FindByEmailAsync(string email, bool asTracking = false);
        Task<bool> EmailExistsAsync(string email);
        Task<List<string>> GetRoleNamesAsync(Guid userId);
        Task<Role> FindRoleByNameAsync(string roleName);
        Task AddAsync(User user);
        Task AddUserRoleAsync(UserRole userRole);
    }

    public class UserRepository : IUserRepository
    {
        private readonly ePriTrackerContext _context;

        public UserRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<User> FindByEmailAsync(string email, bool asTracking = false)
        {
            var query = asTracking ? _context.User : _context.User.AsNoTracking();
            return query.FirstOrDefaultAsync(u => u.Email == email);
        }

        public Task<bool> EmailExistsAsync(string email) => _context.User.AnyAsync(u => u.Email == email);

        public Task<List<string>> GetRoleNamesAsync(Guid userId)
        {
            // Lấy danh sách Role của user từ bảng trung gian UserRole
            return _context.UserRole
                .Where(ur => ur.UserId == userId)
                .Join(_context.Role,
                      ur => ur.RoleId,
                      r => r.RoleId,
                      (ur, r) => r.RoleName)
                .ToListAsync();
        }

        public Task<Role> FindRoleByNameAsync(string roleName) => _context.Role.FirstOrDefaultAsync(r => r.RoleName == roleName);

        public async Task AddAsync(User user) => await _context.User.AddAsync(user);

        public async Task AddUserRoleAsync(UserRole userRole) => await _context.UserRole.AddAsync(userRole);
    }
}
