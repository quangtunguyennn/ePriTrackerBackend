// Controllers/PriceHistoryController.cs
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Services;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    [ApiController]
    [Route("api/products/{productId:guid}/price-history")]
    public class PriceHistoryController : ControllerBase
    {
        private readonly IPriceHistoryService _service;

        public PriceHistoryController(IPriceHistoryService service)
            => _service = service ?? throw new ArgumentNullException(nameof(service));

        /// <summary>GET api/products/{id}/price-history?days=30 (days=0: toàn bộ).</summary>
        [HttpGet]
        [ProducesResponseType(typeof(PriceHistoryDTO), StatusCodes.Status200OK)]
        public async Task<IActionResult> Get(Guid productId, [FromQuery] int days = 30, CancellationToken cancellationToken = default)
        {
            days = Math.Clamp(days, 0, 1095);

            var result = await _service.GetAsync(productId, days, cancellationToken);
            return result == null ? NotFound(new { message = "Không tìm thấy sản phẩm." }) : Ok(result);
        }
    }
}