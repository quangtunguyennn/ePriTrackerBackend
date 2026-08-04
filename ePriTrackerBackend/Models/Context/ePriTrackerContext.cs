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


    }
}
