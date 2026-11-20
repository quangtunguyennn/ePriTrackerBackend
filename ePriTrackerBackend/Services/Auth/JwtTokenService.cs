using ePriTrackerBackend.Models.Entities;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ePriTrackerBackend.Extensions;

namespace ePriTrackerBackend.Services.Auth
{
    public interface IJwtTokenService
    {
        string CreateToken(User user, IEnumerable<string> roles);
    }

    public class JwtTokenService : IJwtTokenService
    {
        private readonly IConfiguration _configuration;
        private readonly byte[] _signingKey;
        private readonly int _lifetimeMinutes;

        public JwtTokenService(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            // Khoá lấy từ User Secrets / biến môi trường (không còn trong appsettings.json); thời hạn theo "JwtSettings:DurationInMinutes"
            _signingKey = SecretsConfiguration.GetJwtSigningKey(configuration);
            _lifetimeMinutes = SecretsConfiguration.GetTokenLifetimeMinutes(configuration);
        }

        public string CreateToken(User user, IEnumerable<string> roles)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Email)
            };

            // Thêm tất cả các role vào Claims để [Authorize(Roles = "...")] hoạt động chính xác
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var key = new SymmetricSecurityKey(_signingKey);
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["JwtSettings:Issuer"],
                audience: _configuration["JwtSettings:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_lifetimeMinutes),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
