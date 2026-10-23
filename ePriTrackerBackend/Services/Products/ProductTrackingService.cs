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

    public interface IProductTrackingService
    {
        Task AddProductAsync(string productLink, string userEmail, decimal? TargetPrice);
        Task<TrackDealResult> TrackDealAsync(Guid userId, TrackDealDto deal);
        /// <summary>Danh sách theo dõi kèm giá ban đầu CỦA người dùng này (bảng Item).</summary>
        Task<List<TrackedProductDTO>> GetTrackedProductsAsync(string userEmail);

        /// <summary>Một sản phẩm kèm giá ban đầu của người dùng (Admin xem sản phẩm không theo dõi → InitialPrice null). Không có → null.</summary>
        Task<TrackedProductDTO> GetTrackedProductAsync(Guid productId, string userEmail);

        Task<Product> GetProductAsync(Guid productId);
        Task<bool> UntrackProductAsync(Guid productId, string userEmail);
        Task<bool> CanAccessProductAsync(Guid productId, string userEmail, bool isAdmin);
    }

    public class ProductTrackingService : IProductTrackingService
    {
        public const string InvalidLinkMessage = "Link sản phẩm không hợp lệ hoặc sàn chưa được hỗ trợ.";

        private static readonly Regex CampaignCodeRegex = new Regex(@"itm_campaign=([^&]+)", RegexOptions.Compiled);

        private readonly IUserRepository _users;
        private readonly IProductRepository _products;
        private readonly ITrackedItemRepository _trackedItems;
        private readonly IEventRepository _events;
        private readonly IPriceHistoryRepository _priceHistories;
        private readonly IPlatformScraperResolver _scrapers;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<ProductTrackingService> _logger;

        public ProductTrackingService(
            IUserRepository users,
            IProductRepository products,
            ITrackedItemRepository trackedItems,
            IEventRepository events,
            IPriceHistoryRepository priceHistories,
            IPlatformScraperResolver scrapers,
            IUnitOfWork unitOfWork,
            ILogger<ProductTrackingService> logger)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _products = products ?? throw new ArgumentNullException(nameof(products));
            _trackedItems = trackedItems ?? throw new ArgumentNullException(nameof(trackedItems));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _priceHistories = priceHistories ?? throw new ArgumentNullException(nameof(priceHistories));
            _scrapers = scrapers ?? throw new ArgumentNullException(nameof(scrapers));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddProductAsync(string productLink, string userEmail, decimal? TargetPrice)
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

            if (!_scrapers.TryResolve(productLink, out var scraper)
                || !scraper.TryNormalizeProductUrl(productLink, out var normalizedLink))
            {
                _logger.LogWarning("Link sản phẩm không hợp lệ hoặc sàn chưa hỗ trợ: {ProductLink}", productLink);
                throw new Exception(InvalidLinkMessage);
            }

            var product = await _products.FindByLinkAsync(normalizedLink);
            if (product == null)
            {
                _logger.LogInformation("Sản phẩm chưa có trong DB, tiến hành lấy dữ liệu từ {Platform}: {ProductLink}", scraper.Platform, normalizedLink);

                product = await ScrapeNewProductAsync(scraper, normalizedLink);
                _products.Add(product);

                AddInitialPriceHistory(product);

                await _unitOfWork.SaveChangesAsync();
            }

            if (await _trackedItems.ExistsAsync(user.UserId, product.ProductId)) return;

            // Giá ban đầu tính theo TỪNG người: giá tại lúc người này bắt đầu theo dõi (hết hàng → null, job giá điền khi có giá)
            // CẬP NHẬT TẠI ĐÂY: Lưu cả InitialPrice và TargetPrice vào bảng Item
            _trackedItems.Add(new Item
            {
                UserId = user.UserId,
                ProductId = product.ProductId,
                InitialPrice = CurrentPriceOrNull(product), // Hàm lấy giá hiện tại của bạn
                TargetPrice = TargetPrice                   // Lưu giá mục tiêu từ tham số truyền vào
            });

            await _unitOfWork.SaveChangesAsync();

            await LinkToCampaignEventAsync(productLink, product.ProductId);

            _logger.LogInformation("Đã thêm sản phẩm {ProductId} vào danh sách theo dõi của User {UserId}", product.ProductId, user.UserId);
        }

        public async Task<TrackDealResult> TrackDealAsync(Guid userId, TrackDealDto deal)
        {
            if (!_scrapers.TryResolve(deal.ProductLink, out var scraper)
                || !scraper.TryNormalizeProductUrl(deal.ProductLink, out var normalizedLink))
            {
                _logger.LogWarning("Link deal không hợp lệ hoặc sàn chưa hỗ trợ: {ProductLink}", deal.ProductLink);
                throw new Exception(InvalidLinkMessage);
            }

            var product = await _products.FindByLinkAsync(normalizedLink);
            if (product == null)
            {
                product = await TryScrapeNewProductAsync(scraper, normalizedLink) ?? ProductFromDeal(deal, normalizedLink);
                _products.Add(product);

                AddInitialPriceHistory(product);

                await _unitOfWork.SaveChangesAsync();
            }

            if (await _trackedItems.ExistsAsync(userId, product.ProductId))
                return TrackDealResult.AlreadyTracking;

            _trackedItems.Add(new Item
            {
                ItemId = Guid.NewGuid(),
                UserId = userId,
                ProductId = product.ProductId,
                InitialPrice = CurrentPriceOrNull(product)
            });
            await _unitOfWork.SaveChangesAsync();

            return TrackDealResult.Added;
        }

        public async Task<List<TrackedProductDTO>> GetTrackedProductsAsync(string userEmail)
        {
            var user = await _users.FindByEmailAsync(userEmail);
            if (user == null) return new List<TrackedProductDTO>();

            return (await _trackedItems.GetByUserWithProductsAsync(user.UserId))
                .Where(i => i.Product != null)
                .Select(i => TrackedProductDTO.From(i.Product, i))
                .ToList();
        }

        public async Task<TrackedProductDTO> GetTrackedProductAsync(Guid productId, string userEmail)
        {
            var product = await _products.FindByIdAsync(productId);
            if (product == null) return null;

            var user = await _users.FindByEmailAsync(userEmail);
            var item = user == null ? null : await _trackedItems.FindAsync(user.UserId, productId);
            return TrackedProductDTO.From(product, item);
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

        private async Task<Product> TryScrapeNewProductAsync(IPlatformScraper scraper, string normalizedLink)
        {
            try
            {
                return await ScrapeNewProductAsync(scraper, normalizedLink);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không cào được {Url} khi theo dõi deal, dùng thông tin của deal: {Message}", normalizedLink, ex.Message);
                return null;
            }
        }

        private static Product ProductFromDeal(TrackDealDto deal, string normalizedLink) => new Product
        {
            ProductId = Guid.NewGuid(),
            ProductName = deal.ProductName,
            ProductLink = normalizedLink,
            ImageURL = deal.ImageURL,
            LatestPrice = deal.CurrentPrice,
            AddedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        private static decimal? CurrentPriceOrNull(Product product)
            => product.LatestPrice > 0 ? product.LatestPrice : null;

        /// <summary>Điểm đầu tiên của biểu đồ giá. Chưa có giá (hết hàng) → không ghi, job giá ghi khi có giá.</summary>
        private void AddInitialPriceHistory(Product product)
        {
            if (CurrentPriceOrNull(product) is not decimal price) return;

            _priceHistories.Add(new PriceHistory
            {
                ProductId = product.ProductId,
                Price = price,
                CheckedAt = product.LastUpdatedAt ?? DateTimeOffset.UtcNow
            });
        }

        private static async Task<Product> ScrapeNewProductAsync(IPlatformScraper scraper, string normalizedLink)
        {
            var scraped = await scraper.GetProductAsync(normalizedLink);
            decimal price = scraped.Price ?? 0;
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

            return new Product
            {
                ProductId = Guid.NewGuid(),
                ProductLink = normalizedLink,
                ProductName = scraped.Name ?? "Sản phẩm không xác định",
                ImageURL = scraped.ImageUrl ?? "",
                Description = ProductDescription.Sanitize(scraped.Description),
                AddedAt = DateTime.UtcNow,
                LatestPrice = price,
                LastUpdatedAt = now,
                IsInStock = scraped.IsInStock,
                OutOfStockSince = scraped.IsInStock ? null : now
            };
        }

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