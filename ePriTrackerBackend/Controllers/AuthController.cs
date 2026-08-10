using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly ePriTrackerContext _context;
        private readonly IConfiguration _configuration;
        public AuthController(ePriTrackerContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpPost("/api/auth/login")]
        public async Task<IActionResult> login(LoginRequestDTO request)
        {
            var user = _context.User.FirstOrDefault(u => u.Email == request.Email && u.Password == request.Password);

            if (user == null)
                return Unauthorized();

            var userRole = user.Role;
            var token = GenerateJwtToken(user);
            return Ok(new { token, userRole });
        }
        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        };
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JwtSettings:SecretKey"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["JwtSettings:Issuer"],
                audience: _configuration["JwtSettings:Audience"],
                claims: claims,
                expires: DateTime.Now.AddMinutes(60),
                signingCredentials: creds
            );
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        

        [HttpGet("/api/auth/me")]
        [Authorize(Roles ="User")]
        public async Task<IActionResult> getMe()
        {
            var currentUserEmail = User?.Identity?.Name;

            var user = await _context.User.FirstOrDefaultAsync(x => x.Email == currentUserEmail);

            if(user == null) return NotFound();

            var userName = user.FirstName + " " + user.LastName;
            var userEmail = user.Email;


            return Ok(new
            {
                UserName = userName,
                UserEmail = userEmail
            });
        }
    }
}
