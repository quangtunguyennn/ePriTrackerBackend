using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    [ApiController]
    [Route("api/admin/metrics")] // Đường dẫn API
    public class MetricsController : ControllerBase
    {
        private readonly IScraperMetricsReader _metrics;

        // Tiêm IScraperMetricsReader vào Controller (chỉ cần quyền đọc số liệu)
        public MetricsController(IScraperMetricsReader metrics)
        {
            _metrics = metrics;
        }

        // Endpoint GET: /api/admin/metrics/scraper
        [HttpGet("scraper")]
        [Authorize(Roles = "Admin")]
        public IActionResult GetScraperMetrics()
        {
            // Trả về thẳng file JSON số liệu đẹp mắt
            return Ok(_metrics.GetMetrics());
        }
    }
}
