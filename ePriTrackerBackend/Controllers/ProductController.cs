using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")] // Route sẽ là: /api/Product
    [ApiController]
    public class ProductController : ControllerBase
    {
        private readonly IProductRepository _repository;
        public ProductController(IProductRepository repository)
        {
            _repository = repository;
        }

        // Endpoint: POST /api/Product/add
        [HttpPost("add")]
        [Authorize(Roles = "User")] // Thêm sản phẩm thường cấp quyền User, nếu là Admin thì bạn tự chỉnh nhé
        public async Task<IActionResult> AddProduct([FromBody] string productLink)
        {
            try
            {
                var userEmail = User?.Identity?.Name;
                if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

                await _repository.AddProduct(productLink, userEmail);
                return Ok(new { message = "Thêm sản phẩm vào danh sách theo dõi thành công!" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Endpoint: GET /api/Product/getAll
        [HttpGet("getAll")] // Dùng HttpGet thay vì HttpPost
        [Authorize(Roles = "User")]
        public async Task<IActionResult> GetAll()
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

            var products = await _repository.getAll(userEmail);
            return Ok(products);
        }

        [HttpGet("/product")]
        //[Authorize(Roles ="User, Admin")]
        public async Task<Product> getById([FromQuery]Guid id)
        {
            var product = await _repository.getById(id);

            return product;
        }

        [HttpGet("suggestion/{productId}")]
        public async Task<IActionResult> getAllBetterProducts(Guid productId)
        {
            try
            {
                // Controller gọi xuống Repository
                var result = await _repository.getAllBetterProducts(productId);

                // Trả về HTTP Status 200 (Thành công) kèm dữ liệu
                return Ok(result);
            }
            catch (Exception ex)
            {
                // Trả về HTTP Status 400 (Lỗi) kèm câu thông báo lỗi
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}