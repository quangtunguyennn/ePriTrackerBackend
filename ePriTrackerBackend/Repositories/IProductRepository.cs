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

        /// <summary>Có theo dõi thay đổi – sửa rồi lưu bằng IUnitOfWork.</summary>
        Task<Product> FindForUpdateAsync(Guid productId);

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

        /// <summary>
        /// Sản phẩm đến hạn cào theo Tier (có người theo dõi, NextCrawlAt ≤ <paramref name="now"/>), hạn sớm nhất trước.
        /// Có theo dõi thay đổi – job cập nhật giá + lịch cào rồi lưu.
        /// </summary>
        Task<List<Product>> GetDueForCrawlAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default);
    }
}