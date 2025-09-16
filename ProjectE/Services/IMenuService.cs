using ProjectE.Models.Entities;
using System.Collections.Generic;

namespace ProjectE.Services
{
    /// <summary>
    /// Interface defining menu service operations.
    /// </summary>
    public interface IMenuService
    {
        List<MenuItemEntity> GetAllMenuItems();
        MenuItemEntity GetMenuItemById(int id);
        void AddMenuItem(MenuItemEntity item);
        void UpdateMenuItem(MenuItemEntity item);
        void DeleteMenuItem(int id);
    }
}
