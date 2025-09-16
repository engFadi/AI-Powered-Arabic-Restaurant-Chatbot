using Microsoft.EntityFrameworkCore;
using ProjectE.Data;
using ProjectE.Models.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ProjectE.Services
{
    public interface IRecommendationService
    {
        Task<List<MenuItemEntity>> GetTopSellingItemsAsync(int limit = 3);

        // 🔹 دالة جديدة: أفضل صنف طلبه مستخدم معيّن
        Task<MenuItemEntity?> GetUserTopItemAsync(int userId);
    }

    public class RecommendationService : IRecommendationService
    {
        private readonly ApplicationDbContext _db;

        public RecommendationService(ApplicationDbContext db)
        {
            _db = db;
        }

        // أفضل N أصناف مبيعا (عامة لكل المستخدمين)
        public async Task<List<MenuItemEntity>> GetTopSellingItemsAsync(int limit = 3)
        {
            var groupedData = await _db.OrderItems
                .GroupBy(oi => oi.MenuItemId)
                .Select(g => new
                {
                    MenuItemId = g.Key,
                    TotalSold = g.Sum(x => x.Quantity)
                })
                .OrderByDescending(x => x.TotalSold)
                .Take(limit)
                .ToListAsync();

            var itemIds = groupedData.Select(x => x.MenuItemId).ToList();

            var topItems = await _db.MenuItems
                .Where(m => itemIds.Contains(m.Id))
                .ToListAsync();

            topItems = topItems
                .OrderByDescending(m => groupedData.First(g => g.MenuItemId == m.Id).TotalSold)
                .ToList();

            return topItems;
        }

        // 🔹 أفضل صنف بطلبه مستخدم معيّن
        public async Task<MenuItemEntity?> GetUserTopItemAsync(int userId)
        {
            var topUserItem = await _db.OrderItems
           .Include(oi => oi.MenuItem) // تأكد من تضمين MenuItem
           .Where(oi => oi.Order.UserId == userId)
           .GroupBy(oi => oi.MenuItem) // قم بالتجميع حسب كائن MenuItem مباشرة
           .Select(g => new
           {
               MenuItem = g.Key, // g.Key هو الآن كائن MenuItemEntity
               TotalOrdered = g.Sum(oi => oi.Quantity)
           })
         .OrderByDescending(x => x.TotalOrdered)
         .FirstOrDefaultAsync();


            return topUserItem?.MenuItem;
        }
    }
}
