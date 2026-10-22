using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Tests.Helpers
{
    /// <summary>
    /// DB SQLite trong RAM cho từng test: repository chạy thật (query, transaction, ExecuteDelete)
    /// mà không đụng tới LocalDB. Mở kết nối suốt vòng đời test để DB không bị xoá.
    /// </summary>
    internal sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ePriTrackerContext> _options;

        public TestDb()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<ePriTrackerContext>().UseSqlite(_connection).Options;

            Context = new ePriTrackerContext(_options);
            Context.Database.EnsureCreated();
        }

        /// <summary>Context mà service dùng.</summary>
        public ePriTrackerContext Context { get; }

        /// <summary>Context mới để kiểm tra dữ liệu đã thật sự được lưu xuống DB.</summary>
        public ePriTrackerContext Fresh() => new ePriTrackerContext(_options);

        public User AddUser(string email, string password = "x", params string[] roles)
        {
            var user = new User { UserId = Guid.NewGuid(), FirstName = "Test", LastName = "User", Email = email, Password = password, IsActive = true };
            Context.User.Add(user);

            foreach (var roleName in roles)
            {
                var role = Context.Role.Local.FirstOrDefault(r => r.RoleName == roleName) ?? Context.Role.FirstOrDefault(r => r.RoleName == roleName);
                if (role == null)
                {
                    role = new Role { RoleName = roleName };
                    Context.Role.Add(role);
                    Context.SaveChanges();
                }
                Context.UserRole.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.UserId, RoleId = role.RoleId });
            }

            Context.SaveChanges();
            return user;
        }

        public Role AddRole(string roleName)
        {
            var role = new Role { RoleName = roleName };
            Context.Role.Add(role);
            Context.SaveChanges();
            return role;
        }

        public Product AddProduct(string link, decimal price = 1000, DateTime? addedAt = null, string name = "Sản phẩm test")
        {
            var product = new Product
            {
                ProductId = Guid.NewGuid(),
                ProductName = name,
                ProductLink = link,
                ImageURL = "img.jpg",
                InitialPrice = price,
                LatestPrice = price,
                AddedAt = addedAt ?? DateTime.UtcNow
            };
            Context.Product.Add(product);
            Context.SaveChanges();
            return product;
        }

        public void Track(User user, Product product)
        {
            Context.Item.Add(new Item { ItemId = Guid.NewGuid(), UserId = user.UserId, ProductId = product.ProductId });
            Context.SaveChanges();
        }

        public Event AddEvent(string title, string link, bool published = true, DateTime? createdAt = null, string platform = "Tiki", DateTimeOffset? endsAt = null)
        {
            var evt = new Event
            {
                EventId = Guid.NewGuid(),
                Title = title,
                EventLink = link,
                ImageUrl = "e.jpg",
                IsPublished = published,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                Platform = platform,
                EndsAt = endsAt
            };
            Context.Event.Add(evt);
            Context.SaveChanges();
            return evt;
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }
}
