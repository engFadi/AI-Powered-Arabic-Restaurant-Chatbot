using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectE.Data;
using ProjectE.Models.Auth;
using ProjectE.Models.Entities;
using ProjectE.Services;
using System.Threading.Tasks;

namespace ProjectE.Controllers.Api
{
    [ApiController]
    [Route("api/auth")]
    public class AuthApiController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ITokenService _tokenService;

        public AuthApiController(ApplicationDbContext context, ITokenService tokenService)
        {
            _context = context;
            _tokenService = tokenService;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return BadRequest(new { message = "Invalid request" });

            var normalizedUsername = model.Username.Trim().ToLower();

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.UserName == normalizedUsername && u.Password == model.Password);

            if (user == null)
            {
                return Unauthorized(new { message = "Invalid credentials." });
            }

            var token = _tokenService.GenerateToken(user);

            return Ok(new
            {
                token,
                userId = user.UserId,
                role = user.Role,
                username = user.UserName
            });
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var normalizedUsername = model.Username.Trim().ToLower();
            var normalizedEmail = model.Email.Trim().ToLower();

            if (await _context.Users.AnyAsync(u => u.UserName == normalizedUsername))
            {
                return Conflict(new { message = "Username already exists." });
            }

            if (await _context.Users.AnyAsync(u => u.Email == normalizedEmail))
            {
                return Conflict(new { message = "Email already exists." });
            }

            var user = new UserEntity
            {
                UserId = Guid.NewGuid().ToString(),
                UserName = normalizedUsername,
                Email = normalizedEmail,
                PhoneNumber = model.PhoneNumber,
                Password = model.Password,
                Role = "Customer"
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return Ok(new { message = "User registered successfully.", username = user.UserName });
        }
    }
}