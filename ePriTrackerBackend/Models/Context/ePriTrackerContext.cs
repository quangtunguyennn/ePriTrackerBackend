using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;

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
        public DbSet<Event> Event { get; set; }
        public DbSet<EventProduct> EventProduct { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EventProduct>()
                .HasKey(ep => new { ep.EventId, ep.ProductId });

            base.OnModelCreating(modelBuilder);
        }
    }
}
