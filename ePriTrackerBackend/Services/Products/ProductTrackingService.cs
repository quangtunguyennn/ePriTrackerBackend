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
        Task<List<TrackedProductDTO>> GetTrackedProductsAsync(string userEmail);
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

        private static readonly TimeZoneInfo VietnamTz = GetVietnamTimeZone();

        private static TimeZoneInfo GetVietnamTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        }

        private static DateTimeOffset GetNowVn() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, VietnamTz);

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
            if (string.IsNullOrWhiteSpace(productLink)) throw new ArgumentException("Link sản phẩm không được trống.", nameof(productLink));
            if (string.IsNullOrWhiteSpace(userEmail)) throw new ArgumentException("Email người dùng không được trống.", nameof(userEmail));

            var user = await _users.FindByEmailAsync(userEmail);
            if (user == null) throw new Exception("Không tìm thấy thông tin người dùng.");

            if (!_scrapers.TryResolve(productLink, out var scraper) || !scraper.TryNormalizeProductUrl(productLink, out var normalizedLink))
                throw new Exception(InvalidLinkMessage);

            var product = await _products.FindByLinkAsync(normalizedLink);
            bool isNewProduct = false;

            if (product == null)
            {
                isNewProduct = true;
                product = await ScrapeNewProductAsync(scraper, normalizedLink);
                _products.Add(product);
                AddInitialPriceHistory(product);
            }

            if (await _trackedItems.ExistsAsync(user.UserId, product.ProductId)) return;

            _trackedItems.Add(new Item
            {
                UserId = user.UserId,
                ProductId = product.ProductId,
                InitialPrice = CurrentPriceOrNull(product),
                TargetPrice = TargetPrice
            });

            // LOGIC CẬP NHẬT CRONJOB & TIER
            product.ActiveTrackersCount++;

            if (product.ActiveTrackersCount == 1)
            {
                product.NextCrawlAt = GetNowVn(); // Giờ VN
                product.CurrentTier = 3;
                product.CrawlIntervalMinutes = 1440;
            }

            if (TargetPrice.HasValue && (!product.MaxTargetPrice.HasValue || TargetPrice.Value > product.MaxTargetPrice.Value))
            {
                product.MaxTargetPrice = TargetPrice.Value;
            }

            if (!isNewProduct) _products.Update(product);

            await _unitOfWork.SaveChangesAsync();
            await LinkToCampaignEventAsync(productLink, product.ProductId);
        }

        public async Task<TrackDealResult> TrackDealAsync(Guid userId, TrackDealDto deal)
        {
            if (!_scrapers.TryResolve(deal.ProductLink, out var scraper) || !scraper.TryNormalizeProductUrl(deal.ProductLink, out var normalizedLink))
                throw new Exception(InvalidLinkMessage);

            var product = await _products.FindByLinkAsync(normalizedLink);
            bool isNewProduct = false;

            if (product == null)
            {
                isNewProduct = true;
                product = await TryScrapeNewProductAsync(scraper, normalizedLink) ?? ProductFromDeal(deal, normalizedLink);
                _products.Add(product);
                AddInitialPriceHistory(product);
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

            product.ActiveTrackersCount++;
            if (product.ActiveTrackersCount == 1)
            {
                product.NextCrawlAt = GetNowVn(); // Giờ VN
                product.CurrentTier = 3;
                product.CrawlIntervalMinutes = 1440;
            }

            if (!isNewProduct) _products.Update(product);

            await _unitOfWork.SaveChangesAsync();
            return TrackDealResult.Added;
        }

        public async Task<bool> UntrackProductAsync(Guid productId, string userEmail)
        {
            var user = await _users.FindByEmailAsync(userEmail);
            if (user == null) throw new Exception("Không tìm thấy người dùng");

            var item = await _trackedItems.FindAsync(user.UserId, productId);
            if (item == null) throw new Exception("Sản phẩm không tồn tại trong danh sách theo dõi.");

            _trackedItems.Remove(item);

            var product = await _products.FindByIdAsync(productId);
            if (product != null && product.ActiveTrackersCount > 0)
            {
                product.ActiveTrackersCount--;

                if (product.ActiveTrackersCount == 0)
                {
                    product.CurrentTier = 4;
                    product.CrawlIntervalMinutes = 43200;
                    product.NextCrawlAt = GetNowVn().AddDays(30); // Giờ VN
                    product.MaxTargetPrice = null;
                }
                else if (item.TargetPrice.HasValue && product.MaxTargetPrice == item.TargetPrice.Value)
                {
                    product.NextCrawlAt = GetNowVn(); // Giờ VN
                }

                _products.Update(product);
            }

            await _unitOfWork.SaveChangesAsync();
            return true;
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

        public async Task<bool> CanAccessProductAsync(Guid productId, string userEmail, bool isAdmin)
        {
            if (isAdmin) return true;
            var user = await _users.FindByEmailAsync(userEmail);
            return user != null && await _trackedItems.ExistsAsync(user.UserId, productId);
        }

        private async Task<Product> TryScrapeNewProductAsync(IPlatformScraper scraper, string normalizedLink)
        {
            try { return await ScrapeNewProductAsync(scraper, normalizedLink); }
            catch (Exception ex)
            {
                _logger.LogWarning("Không cào được {Url} khi theo dõi deal: {Message}", normalizedLink, ex.Message);
                return null;
            }
        }

        private static Product ProductFromDeal(TrackDealDto deal, string normalizedLink)
        {
            var nowVn = GetNowVn();
            return new Product
            {
                ProductId = Guid.NewGuid(),
                ProductName = deal.ProductName,
                ProductLink = normalizedLink,
                ImageURL = deal.ImageURL,
                LatestPrice = deal.CurrentPrice,
                AddedAt = nowVn.DateTime, // Giờ VN
                LastUpdatedAt = nowVn,    // Giờ VN
                NextCrawlAt = nowVn,      // Giờ VN
                CurrentTier = 3,
                CrawlIntervalMinutes = 1440
            };
        }

        private static decimal? CurrentPriceOrNull(Product product) => product.LatestPrice > 0 ? product.LatestPrice : null;

        private void AddInitialPriceHistory(Product product)
        {
            if (CurrentPriceOrNull(product) is not decimal price) return;
            _priceHistories.Add(new PriceHistory
            {
                ProductId = product.ProductId,
                Price = price,
                CheckedAt = product.LastUpdatedAt ?? GetNowVn() // Giờ VN
            });
        }

        private static async Task<Product> ScrapeNewProductAsync(IPlatformScraper scraper, string normalizedLink)
        {
            var scraped = await scraper.GetProductAsync(normalizedLink);
            decimal price = scraped.Price ?? 0;
            var nowVn = GetNowVn();

            return new Product
            {
                ProductId = Guid.NewGuid(),
                ProductLink = normalizedLink,
                ProductName = scraped.Name ?? "Sản phẩm không xác định",
                ImageURL = scraped.ImageUrl ?? "",
                Description = ProductDescription.Sanitize(scraped.Description),
                AddedAt = nowVn.DateTime, // Giờ VN
                LatestPrice = price,
                LastUpdatedAt = nowVn,    // Giờ VN
                IsInStock = scraped.IsInStock,
                OutOfStockSince = scraped.IsInStock ? null : nowVn, // Giờ VN

                ActiveTrackersCount = 0,
                CurrentTier = 3,
                CrawlIntervalMinutes = 1440,
                NextCrawlAt = null
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

            _events.AddProduct(new EventProduct { EventId = matchedEvent.EventId, ProductId = productId });
            await _unitOfWork.SaveChangesAsync();
        }
    }
}