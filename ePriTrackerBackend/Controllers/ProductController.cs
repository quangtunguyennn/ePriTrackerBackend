using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services.Products;
using ePriTrackerBackend.Services.Suggestions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")] // Route sẽ là: /api/Product
    [ApiController]
    public class ProductController : ControllerBase
    {
        private readonly IProductTrackingService _tracking;
        private readonly ISuggestionService _suggestions;

        public ProductController(IProductTrackingService tracking, ISuggestionService suggestions)
        {
            _tracking = tracking;
            _suggestions = suggestions;
        }

        // Endpoint: POST /api/product/add
        [HttpPost("/api/product/add")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> AddProduct([FromBody] string productLink)
        {
            try
            {
                var userEmail = User?.Identity?.Name;
                if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

                await _tracking.AddProductAsync(productLink, userEmail);
                return Ok(new { message = "Thêm sản phẩm vào danh sách theo dõi thành công!" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Endpoint: GET /api/product/all
        [HttpGet("/api/product/all")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> GetAll()
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

            var products = await _tracking.GetTrackedProductsAsync(userEmail);
            return Ok(products);
        }

        // Endpoint: GET /product?id=...
        [HttpGet("/product")]
        [Authorize(Roles = "User, Admin")]
        public async Task<ActionResult<Product>> GetById([FromQuery] Guid id)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
            if (!await CanAccessAsync(id, userEmail)) return ProductNotFound();

            var product = await _tracking.GetProductAsync(id);

            if (product == null) return NotFound(new { message = "Không tìm thấy sản phẩm." });

            return product;
        }

        // Endpoint: GET /api/product/suggestion/{productId}
        [Authorize(Roles = "User")]
        [HttpGet("/api/product/suggestion/{productId}")]
        public async Task<IActionResult> GetAllBetterProducts(Guid productId)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
            if (!await CanAccessAsync(productId, userEmail)) return ProductNotFound();
            try
            {
                var result = await _suggestions.GetSuggestionsAsync(productId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Endpoint: DELETE /api/product/delete?id=...
        [Authorize(Roles = "User")]
        [HttpDelete("/api/product/delete")]
        public async Task<IActionResult> DeleteProduct([FromQuery] Guid id)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

            try
            {
                bool isDeleted = await _tracking.UntrackProductAsync(id, userEmail);

                if (!isDeleted)
                {
                    return BadRequest("delete error");
                }

                return Ok(new { message = "Delete successfully" });
            }
            catch (Exception ex)
            {
                // Bắt lỗi nghiệp vụ (như "Sản phẩm không tồn tại")
                return BadRequest(new { message = ex.Message });
            }
        }

        [Authorize(Roles = "User")]
        [HttpGet("/api/product/refresh-suggestions")]
        public async Task<IActionResult> RefreshSuggestions(Guid productId)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
            if (!await CanAccessAsync(productId, userEmail)) return ProductNotFound();

            var suggestions = await _suggestions.RefreshSuggestionsAsync(productId);
            return Ok(suggestions);
        }

        // Trả 404 (không phải 403) để không lộ việc sản phẩm có tồn tại hay không
        private Task<bool> CanAccessAsync(Guid productId, string userEmail)
            => _tracking.CanAccessProductAsync(productId, userEmail, User.IsInRole("Admin"));

        private NotFoundObjectResult ProductNotFound() => NotFound(new { message = "Không tìm thấy sản phẩm." });
    }
}
