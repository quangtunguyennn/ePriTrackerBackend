using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Repositories
{
    public interface IProductRepository
    {
        public Task AddProduct(string productLink, string userEmail);
        public Task<List<Product>> getAll(string userEmail);
        public Task<Product> getById(Guid id);
        public Task<List<SuggestionProductDTO>> getAllBetterProducts(Guid productId);
        public Task<bool> deleteProduct(Guid id);
    }
}
