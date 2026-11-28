using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Services.Products;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly IEventService _eventService;
        private readonly IProductTrackingService _tracking;

        public EventController(IEventService eventService, IProductTrackingService tracking)
        {
            _eventService = eventService;
            _tracking = tracking;
        }

        // =======================================================
        // PHẦN 1: API DÀNH CHO ADMIN QUẢN LÝ SỰ KIỆN
        // =======================================================

        [HttpGet("crawlPreview")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CrawlPreviewEvents()
        {
            try
            {
                return Ok(await _eventService.GetPreviewAsync());
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("postEvent")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> PostEvent([FromBody] Event tikiEvent)
        {
            try
            {
                var result = await _eventService.PublishAsync(tikiEvent);
                string message = result == PublishEventResult.Created
                    ? "Event published successfully!"
                    : "This event already exists; its visibility has been updated!";

                // Cào ngay để Admin biết sự kiện có sản phẩm không (trang coupon / chưa mở bán thì người dùng mở ra sẽ trống)
                int? productCount = await _eventService.CheckLiveProductCountAsync(tikiEvent.EventId);
                string note = productCount switch
                {
                    null => " Loading products from Tiki, check the product count again in a few minutes.",
                    0 => " Note: no products could be read from this page yet (it may be a coupon page or the sale has not started).",
                    _ => $" ({productCount} products)"
                };

                return Ok(new { message = message + note, productCount });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Admin lấy TẤT CẢ sự kiện (cả ẩn và hiện) để quản lý
        [HttpGet("getAllEventsAdmin")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAllEventsAdmin()
        {
            return Ok(await _eventService.GetAllForAdminAsync());
        }

        // Bật/Tắt trạng thái IsPublished (Soft Delete / Gỡ bài / Đăng lại)
        [HttpPut("togglePublish/{eventId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> TogglePublish(Guid eventId)
        {
            var isPublished = await _eventService.TogglePublishAsync(eventId);
            if (isPublished == null) return NotFound(new { message = "Event not found." });

            return Ok(new { message = isPublished.Value ? "Event republished!" : "Event unpublished successfully!" });
        }

        // =======================================================
        // PHẦN 2: API DÀNH CHO USER VÀ ADMIN ĐỂ HIỂN THỊ
        // =======================================================

        [HttpGet("getPublishedEvents")]
        [AllowAnonymous] // Khách xem được sự kiện; theo dõi (trackDeal) mới cần đăng nhập
        public async Task<IActionResult> GetPublishedEvents()
        {
            return Ok(await _eventService.GetPublishedAsync());
        }

        [HttpGet("getLiveEventProducts/{eventId}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetLiveEventProducts(Guid eventId)
        {
            try
            {
                // Khách chỉ đọc cache (không kích hoạt cào sàn); người đã đăng nhập giữ hành vi cũ
                bool isSignedIn = User.Identity?.IsAuthenticated == true;
                var result = await _eventService.GetLiveProductsAsync(eventId, allowScrape: isSignedIn);

                return result.Status switch
                {
                    LiveProductsStatus.EventNotFound => NotFound(new { message = "Event not found." }),
                    LiveProductsStatus.NoProducts => BadRequest(new { message = "This event has ended on the platform or has no products." }),
                    LiveProductsStatus.StillLoading => StatusCode(StatusCodes.Status503ServiceUnavailable,
                        new { message = "This event's products are still loading from the platform. Please try again in a few minutes." }),
                    _ => Ok(result.Products)
                };
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("trackDeal")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> TrackDeal([FromBody] TrackDealDto request)
        {
            try
            {
                // Lấy thông tin User đang đăng nhập từ Token
                var userId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value);

                var result = await _tracking.TrackDealAsync(userId, request);

                if (result == TrackDealResult.AlreadyTracking)
                    return BadRequest(new { message = "You are already tracking this product!" });

                return Ok(new { message = "Added to your tracking list!" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("deleteEvent/{eventId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteEvent(Guid eventId)
        {
            try
            {
                if (!await _eventService.DeleteAsync(eventId))
                    return NotFound(new { message = "Event not found." });

                return Ok(new { message = "Event deleted successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
