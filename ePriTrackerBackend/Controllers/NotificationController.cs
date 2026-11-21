using System.Security.Claims;
using ePriTrackerBackend.Services.Notification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    /// <summary>Chuông thông báo: mỗi người chỉ thấy và sửa được thông báo của mình.</summary>
    [ApiController]
    [Route("api/notifications")]
    [Authorize]
    public class NotificationController : ControllerBase
    {
        private readonly INotificationInboxService _inbox;

        public NotificationController(INotificationInboxService inbox)
        {
            _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        }

        // GET /api/notifications?take=20&before=2026-11-21T10:00:00Z
        [HttpGet]
        public async Task<IActionResult> GetNotifications([FromQuery] int take = NotificationInboxService.DefaultPageSize, [FromQuery] DateTimeOffset? before = null,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            return Ok(await _inbox.GetPageAsync(userId, take, before, cancellationToken));
        }

        // GET /api/notifications/unread-count
        [HttpGet("unread-count")]
        public async Task<IActionResult> GetUnreadCount(CancellationToken cancellationToken)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            return Ok(new { count = await _inbox.GetUnreadCountAsync(userId, cancellationToken) });
        }

        // PUT /api/notifications/{id}/read
        [HttpPut("{id:guid}/read")]
        public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            return await _inbox.MarkReadAsync(userId, id, cancellationToken)
                ? NoContent()
                : NotFound(new { message = "Notification not found." });
        }

        // PUT /api/notifications/read-all
        [HttpPut("read-all")]
        public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized();
            await _inbox.MarkAllReadAsync(userId, cancellationToken);
            return NoContent();
        }

        // JWT có claim NameIdentifier = UserId (JwtTokenService)
        private bool TryGetUserId(out Guid userId)
            => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}
