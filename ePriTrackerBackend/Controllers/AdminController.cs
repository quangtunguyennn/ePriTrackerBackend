using ePriTrackerBackend.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    /// <summary>API trang Admin (thống kê, giám sát cào, sự kiện sàn tự đăng). Chỉ Admin.</summary>
    [ApiController]
    [Route("api/admin")]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly IAdminMonitoringService _monitoring;
        private readonly IAdminStatsService _stats;
        private readonly IAdminEventService _events;

        public AdminController(IAdminMonitoringService monitoring, IAdminStatsService stats, IAdminEventService events)
        {
            _monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _events = events ?? throw new ArgumentNullException(nameof(events));
        }

        public class SetHiddenRequest
        {
            public bool Hidden { get; set; }
        }

        // GET /api/admin/events/auto – sự kiện của các sàn tự đăng + kết quả đồng bộ gần nhất
        [HttpGet("events/auto")]
        public async Task<IActionResult> GetAutoEvents()
            => Ok(await _events.GetAutoEventsAsync());

        // POST /api/admin/events/auto/{platform}/sync – đồng bộ ngay một sàn
        [HttpPost("events/auto/{platform}/sync")]
        public async Task<IActionResult> SyncNow(string platform, CancellationToken cancellationToken)
        {
            var result = await _events.SyncNowAsync(platform, cancellationToken);
            return result == null
                ? NotFound(new { message = "Sàn này không tự đăng sự kiện." })
                : Ok(result);
        }

        // PUT /api/admin/events/auto/{eventId}/hidden – ẩn / hiện một sự kiện tự đăng
        [HttpPut("events/auto/{eventId:guid}/hidden")]
        public async Task<IActionResult> SetHidden(Guid eventId, [FromBody] SetHiddenRequest request)
        {
            var result = await _events.SetHiddenAsync(eventId, request?.Hidden ?? false);
            return result == null
                ? NotFound(new { message = "Không tìm thấy sự kiện tự đăng." })
                : Ok(result);
        }

        // GET /api/admin/stats
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
            => Ok(await _stats.GetStatsAsync(cancellationToken));

        // GET /api/admin/monitoring
        [HttpGet("monitoring")]
        public async Task<IActionResult> GetMonitoring(CancellationToken cancellationToken)
            => Ok(await _monitoring.GetMonitoringAsync(cancellationToken));
    }
}
