using System.Text.RegularExpressions;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Services.Products
{
    public enum TrackDealResult
    {
        Added,
        AlreadyTracking
    }

    /// <summary>
    /// Nghiệp vụ "theo dõi sản phẩm": thêm từ link, theo dõi deal trong sự kiện, xem và bỏ theo dõi.
    /// Lỗi nghiệp vụ ném Exception với message hiển thị được cho người dùng (controller trả 400 { message }).
    /// </summary>
    public interface IProductTrackingService
    {
        Task AddProductAsync(string productLink, string userEmail);
        Task<TrackDealResult> TrackDealAsync(Guid userId, TrackDealDto deal);
        Task<List<Product>> GetTrackedProductsAsync(string userEmail);
        Task<Product> GetProductAsync(Guid productId);
        Task<bool> UntrackProductAsync(Guid productId, string userEmail);

        /// <summary>User chỉ được xem/cào gợi ý cho sản phẩm mình đang theo dõi; Admin được xem tất cả.</summary>
        Task<bool> CanAccessProductAsync(Guid productId, string userEmail, bool isAdmin);
    }

    public class ProductTrackingService : IProductTrackingService
    {
        private static readonly Regex CampaignCodeRegex = new Regex(@"itm_campaign=([^&]+)", RegexOptions.Compiled);

        private readonly IUserRepository _users;
        private readonly IProductRepository _products;
        private readonly ITrackedItemRepository _trackedItems;
        private readonly IEventRepository _events;
        private readonly IPlatformScraperResolver _scrapers;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<ProductTrackingService> _logger;

        public ProductTrackingService(
            IUserRepository users,
            IProductRepository products,
            ITrackedItemRepository trackedItems,
            IEventRepository events,
            IPlatformScraperResolver scrapers,
            IUnitOfWork unitOfWork,
            ILogger<ProductTrackingService> logger)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _products = products ?? throw new ArgumentNullException(nameof(products));
            _trackedItems = trackedItems ?? throw new ArgumentNullException(nameof(trackedItems));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _scrapers = scrapers ?? throw new ArgumentNullException(nameof(scrapers));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddProductAsync(string productLink, string userEmail)
        {
            if (string.IsNullOrWhiteSpace(productLink))
                throw new ArgumentException("Link sản phẩm không được trống.", nameof(productLink));
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new ArgumentException("Email người dùng không được trống.", nameof(userEmail));

            var user = await _users.FindByEmailAsync(userEmail);
            if (user == null)
            {
                _logger.LogWarning("Không tìm thấy người dùng với email: {Email}", userEmail);
                throw new Exception("Không tìm thấy thông tin người dùng.");
            }

            // Chọn sàn theo host của link, rồi chuẩn hoá link để chống trùng sản phẩm trong DB
            if (!_scrapers.TryResolve(productLink, out var scraper)
                || !scraper.TryNormalizeProductUrl(productLink, out var normalizedLink))
            {
                _logger.LogWarning("Link Tiki không hợp lệ: {ProductLink}", productLink);
                throw new Exception("Link Tiki không hợp lệ. Không tìm thấy Product ID."); // Giữ nguyên message cũ – FE hiển thị câu này
            }

            var product = await _products.FindByLinkAsync(normalizedLink);
            if (product == null)
            {
                _logger.LogInformation("Sản phẩm chưa có trong DB, tiến hành lấy dữ liệu từ {Platform}: {ProductLink}", scraper.Platform, normalizedLink);

                product = await ScrapeNewProductAsync(scraper, normalizedLink);
                _products.Add(product);
                await _unitOfWork.SaveChangesAsync();
            }

            // Đã theo dõi rồi thì bỏ qua (khác TrackDeal: không báo lỗi)
            if (await _trackedItems.ExistsAsync(user.UserId, product.ProductId)) return;

            _trackedItems.Add(new Item { UserId = user.UserId, ProductId = product.ProductId });
            await _unitOfWork.SaveChangesAsync();

            await LinkToCampaignEventAsync(productLink, product.ProductId);

            _logger.LogInformation("Đã thêm sản phẩm {ProductId} vào danh sách theo dõi của User {UserId}", product.ProductId, user.UserId);
        }

        public async Task<TrackDealResult> TrackDealAsync(Guid userId, TrackDealDto deal)
        {
            // Deal lấy từ trang sự kiện đã có đủ thông tin → không cần cào lại, lưu nguyên link gốc
            var product = await _products.FindByLinkAsync(deal.ProductLink);
            if (product == null)
            {
                product = new Product
                {
                    ProductId = Guid.NewGuid(),
                    ProductName = deal.ProductName,
                    ProductLink = deal.ProductLink,
                    ImageURL = deal.ImageURL,

                    // Khi mới thêm vào, Giá khởi điểm và Giá mới nhất đều bằng giá hiện tại
                    InitialPrice = deal.CurrentPrice,
                    LatestPrice = deal.CurrentPrice,

                    AddedAt = DateTime.UtcNow,
                    LastUpdatedAt = DateTimeOffset.UtcNow
                };

                _products.Add(product);
                await _unitOfWork.SaveChangesAsync();
            }

            if (await _trackedItems.ExistsAsync(userId, product.ProductId))
                return TrackDealResult.AlreadyTracking;

            _trackedItems.Add(new Item
            {
                ItemId = Guid.NewGuid(),
                UserId = userId,
                ProductId = product.ProductId
            });
            await _unitOfWork.SaveChangesAsync();

            return TrackDealResult.Added;
        }

        public async Task<List<Product>> GetTrackedProductsAsync(string userEmail)
        {
            var user = await _users.FindByEmailAsync(userEmail);
            if (user == null) return new List<Product>();

            return await _products.GetTrackedByUserAsync(user.UserId);
        }

        public Task<Product> GetProductAsync(Guid productId) => _products.FindByIdAsync(productId);

        public async Task<bool> UntrackProductAsync(Guid productId, string userEmail)
        {
            var user = await _users.FindByEmailAsync(userEmail);
            if (user == null) throw new Exception("Không tìm thấy người dùng");

            var item = await _trackedItems.FindAsync(user.UserId, productId);
            if (item == null) throw new Exception("Sản phẩm không tồn tại trong danh sách theo dõi.");

            _trackedItems.Remove(item);
            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        public async Task<bool> CanAccessProductAsync(Guid productId, string userEmail, bool isAdmin)
        {
            if (isAdmin) return true;

            var user = await _users.FindByEmailAsync(userEmail);
            return user != null && await _trackedItems.ExistsAsync(user.UserId, productId);
        }

        private static async Task<Product> ScrapeNewProductAsync(IPlatformScraper scraper, string normalizedLink)
        {
            // Bên trong scraper là Dual-Layer: HttpClient → Playwright
            var scraped = await scraper.GetProductAsync(normalizedLink);
            decimal price = scraped.Price ?? 0;

            return new Product
            {
                ProductLink = normalizedLink,
                ProductName = scraped.Name ?? "Sản phẩm không xác định",
                ImageURL = scraped.ImageUrl ?? "",
                Description = scraped.Description ?? "",
                InitialPrice = price,
                AddedAt = DateTime.UtcNow,
                LatestPrice = price,
                LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
            };
        }

        /// <summary>
        /// Link có tham số itm_campaign (người dùng bấm từ trang sự kiện) → tự gắn sản phẩm vào sự kiện tương ứng.
        /// </summary>
        private async Task LinkToCampaignEventAsync(string productLink, Guid productId)
        {
            var campaignMatch = CampaignCodeRegex.Match(productLink);
            if (!campaignMatch.Success) return;

            string campaignCode = campaignMatch.Groups[1].Value.ToLower();

            var matchedEvent = await _events.FindByLinkContainingAsync(campaignCode);
            if (matchedEvent == null) return;

            if (await _events.HasProductAsync(matchedEvent.EventId, productId)) return;

            _events.AddProduct(new EventProduct
            {
                EventId = matchedEvent.EventId,
                ProductId = productId
            });
            await _unitOfWork.SaveChangesAsync();
        }
    }
}
