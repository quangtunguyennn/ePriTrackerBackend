using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Services.Auth;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace ePriTrackerBackend.Tests.Services
{
    public class AuthServiceTests : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly PasswordService _passwords = new();
        private readonly AuthService _service;

        public AuthServiceTests()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = "test-secret-key-phai-du-dai-it-nhat-32-byte!!",
                ["JwtSettings:Issuer"] = "ePriTracker",
                ["JwtSettings:Audience"] = "ePriTrackerClient"
            }).Build();

            _service = new AuthService(new UserRepository(_db.Context), _passwords, new JwtTokenService(config), new EfUnitOfWork(_db.Context));
        }

        public void Dispose() => _db.Dispose();

        private static registerRequestDTO NewUser(string email = "a@test.vn") => new()
        {
            FirstName = "Anh",
            LastName = "Khoi",
            Email = email,
            Password = "Secret@123"
        };

        [Fact]
        public async Task Register_HashesPassword_AndAssignsUserRole()
        {
            _db.AddRole("User");

            Assert.True(await _service.RegisterAsync(NewUser()));

            using var fresh = _db.Fresh();
            var user = fresh.User.Single(u => u.Email == "a@test.vn");
            Assert.NotEqual("Secret@123", user.Password);
            Assert.Equal(PasswordCheckResult.Success, _passwords.Verify(user, "Secret@123"));
            Assert.Equal("User", fresh.UserRole.Where(ur => ur.UserId == user.UserId).Join(fresh.Role, ur => ur.RoleId, r => r.RoleId, (ur, r) => r.RoleName).Single());
        }

        [Fact]
        public async Task Register_DuplicateEmail_ReturnsFalse()
        {
            _db.AddUser("a@test.vn");

            Assert.False(await _service.RegisterAsync(NewUser("a@test.vn")));
        }

        [Fact]
        public async Task Login_ValidCredentials_ReturnsTokenWithIdEmailAndRoles()
        {
            _db.AddRole("User");
            await _service.RegisterAsync(NewUser());

            var result = await _service.LoginAsync("a@test.vn", "Secret@123");

            Assert.NotNull(result);
            Assert.Equal(new[] { "User" }, result!.Roles);

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);
            var userId = _db.Fresh().User.Single().UserId.ToString();
            // TrackDeal đọc ClaimTypes.NameIdentifier (bug đã sửa ở bước 1); [Authorize(Roles)] đọc ClaimTypes.Role
            Assert.Equal(userId, jwt.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value);
            Assert.Equal("a@test.vn", jwt.Claims.Single(c => c.Type == ClaimTypes.Name).Value);
            Assert.Equal("User", jwt.Claims.Single(c => c.Type == ClaimTypes.Role).Value);
            Assert.Equal("ePriTracker", jwt.Issuer);
            Assert.InRange(jwt.ValidTo, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61)); // hết hạn sau 60 phút
        }

        [Theory]
        [InlineData("a@test.vn", "sai-mat-khau")]
        [InlineData("khong-ton-tai@test.vn", "Secret@123")]
        public async Task Login_InvalidCredentials_ReturnsNull(string email, string password)
        {
            _db.AddRole("User");
            await _service.RegisterAsync(NewUser());

            Assert.Null(await _service.LoginAsync(email, password));
        }

        [Fact]
        public async Task Login_LegacyPlainTextPassword_SucceedsAndRehashes()
        {
            _db.AddUser("cu@test.vn", password: "matkhaucu", "User");

            var result = await _service.LoginAsync("cu@test.vn", "matkhaucu");

            Assert.NotNull(result);
            var stored = _db.Fresh().User.Single(u => u.Email == "cu@test.vn");
            Assert.NotEqual("matkhaucu", stored.Password); // đã được hash lại
            Assert.Equal(PasswordCheckResult.Success, _passwords.Verify(stored, "matkhaucu"));
        }

        [Fact]
        public async Task GetCurrentUser_ReturnsSameShapeAsOldResponse()
        {
            _db.AddUser("me@test.vn", "x", "User", "Admin");

            var me = await _service.GetCurrentUserAsync("me@test.vn");

            Assert.Equal("Test User", me!.UserName);
            Assert.Equal("me@test.vn", me.UserEmail);
            Assert.Equal(new[] { "Admin", "User" }, me.Roles.OrderBy(r => r));
            Assert.Null(await _service.GetCurrentUserAsync("khong-ton-tai@test.vn"));
        }
    }

    public class PasswordServiceTests
    {
        private readonly PasswordService _service = new();
        private static User UserWith(string password) => new() { UserId = Guid.NewGuid(), Email = "x@test.vn", Password = password };

        [Fact]
        public void HashThenVerify_Succeeds()
        {
            var user = UserWith("");
            user.Password = _service.Hash(user, "abc");

            Assert.Equal(PasswordCheckResult.Success, _service.Verify(user, "abc"));
            Assert.Equal(PasswordCheckResult.Failed, _service.Verify(user, "abd"));
        }

        [Theory]
        [InlineData("plain", "plain", PasswordCheckResult.SuccessRehashNeeded)] // tài khoản cũ
        [InlineData("plain", "khac", PasswordCheckResult.Failed)]
        [InlineData("abcd1234", "abcd1234", PasswordCheckResult.SuccessRehashNeeded)] // plain text tình cờ là Base64 hợp lệ
        [InlineData("plain", "", PasswordCheckResult.Failed)]
        [InlineData("", "plain", PasswordCheckResult.Failed)]
        public void Verify_LegacyAndEdgeCases(string stored, string input, PasswordCheckResult expected)
        {
            Assert.Equal(expected, _service.Verify(UserWith(stored), input));
        }
    }
}
