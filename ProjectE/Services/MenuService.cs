using ProjectE.Data;
using ProjectE.Models.Entities;
using System.Collections.Generic;
using System.Linq;

namespace ProjectE.Services
{
    public class MenuService : IMenuService
    {
        private readonly ApplicationDbContext _context;

        public MenuService(ApplicationDbContext context)
        {
            _context = context;
        }

        public List<MenuItemEntity> GetAllMenuItems()
        {
            return _context.MenuItems.ToList();
        }

        public MenuItemEntity GetMenuItemById(int id)
        {
            return _context.MenuItems.FirstOrDefault(m => m.Id == id);
        }

        public void AddMenuItem(MenuItemEntity item)
        {
            _context.MenuItems.Add(item);
            _context.SaveChanges();
        }

        public void UpdateMenuItem(MenuItemEntity item)
        {
            var existing = _context.MenuItems.FirstOrDefault(m => m.Id == item.Id);
            if (existing != null)
            {
                existing.Name = item.Name;
                existing.Description = item.Description;
                existing.Category = item.Category;
                existing.Price = item.Price;
                existing.IsAvailable = item.IsAvailable; // important
                _context.SaveChanges();
            }
        }

        public void DeleteMenuItem(int id)
        {
            var existing = _context.MenuItems.FirstOrDefault(m => m.Id == id);
            if (existing != null)
            {
                _context.MenuItems.Remove(existing);
                _context.SaveChanges();
            }
        }
    }
}
