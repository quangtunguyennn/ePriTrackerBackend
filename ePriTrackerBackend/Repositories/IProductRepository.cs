using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Repositories
{
    /// <summary>
    /// Truy cập bảng Product. Chỉ làm việc với DB – việc cào dữ liệu nằm ở tầng Service/Scraping.
    /// </summary>
    public interface IProductRepository
    {
        /// <summary>Đọc (không theo dõi thay đổi).</summary>
        Task<Product> FindByIdAsync(Guid productId);

        /// <summary>Có theo dõi thay đổi.</summary>
        Task<Product> FindByLinkAsync(string productLink);

        void Add(Product product);

        // BỔ SUNG PHƯƠNG THỨC NÀY
        void Update(Product product);

        /// <summary>Sản phẩm user đang theo dõi, mới thêm trước.</summary>
        Task<List<Product>> GetTrackedByUserAsync(Guid userId);

        /// <summary>Mọi sản phẩm đang có ít nhất một người theo dõi (cho job Hangfire).</summary>
        /// <param name="asTracking">true khi job sẽ cập nhật giá rồi lưu.</param>
        Task<List<Product>> GetAllTrackedAsync(bool asTracking, CancellationToken cancellationToken = default);
    }
}