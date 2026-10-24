using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Models.Context
{
    public class ePriTrackerContext : DbContext
    {
        public ePriTrackerContext(DbContextOptions<ePriTrackerContext> options) : base(options) { }

        public DbSet<User> User { get; set; }
        public DbSet<Product> Product { get; set; }
        public DbSet<Item> Item { get; set; }
        public DbSet<SuggestionProduct> SuggestionProduct { get; set; }

        public DbSet<PriceHistory> PriceHistory { get; set; }
        public DbSet<Role> Role { get; set; }
        public DbSet<UserRole> UserRole { get; set; }

        public DbSet<Event> Event { get; set; }
        public DbSet<EventProduct> EventProduct { get; set; }

        // Bổ sung DbSet cho bảng Notification
        public DbSet<Notification> Notification { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. Cấu hình Khóa chính nâng cao
            modelBuilder.Entity<EventProduct>()
                .HasKey(ep => new { ep.EventId, ep.ProductId });

            // 2. Index cho Cronjob truy vấn sản phẩm siêu tốc (NextCrawlAt + ActiveTrackersCount)
            modelBuilder.Entity<Product>()
                .HasIndex(p => new { p.NextCrawlAt, p.ActiveTrackersCount });

            // 3. Index cho API lấy danh sách thông báo theo User (sắp xếp giảm dần theo thời gian)
            modelBuilder.Entity<Notification>()
                .HasIndex(n => new { n.UserId, n.CreatedAt });
        }
    }
}