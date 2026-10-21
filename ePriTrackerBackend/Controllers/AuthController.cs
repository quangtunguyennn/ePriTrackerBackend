using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("/api/auth/login")]
        public async Task<IActionResult> Login([FromBody] LoginRequestDTO request)
        {
            var result = await _authService.LoginAsync(request.Email, request.Password);

            if (result == null)
                return Unauthorized(new { message = "Invalid email or password." });

            return Ok(new
            {
                token = result.Token,
                userRoles = result.Roles
            });
        }

        [HttpPost("/api/auth/register")]
        public async Task<IActionResult> Register([FromBody] registerRequestDTO request)
        {
            if (!await _authService.RegisterAsync(request))
                return BadRequest(new { message = "Email is already in use." });

            return Ok(new { message = "Register successfully!" });
        }

        [HttpGet("/api/auth/me")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> GetMe()
        {
            var currentUser = await _authService.GetCurrentUserAsync(User?.Identity?.Name);

            if (currentUser == null) return NotFound(new { message = "User not found." });

            return Ok(currentUser);
        }
    }
}
