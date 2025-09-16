using Microsoft.AspNetCore.Mvc;
using ProjectE.Data;
using ProjectE.Models.Entities;
using ProjectE.Models.MenuModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ProjectE.Controllers
{
    /// <summary>
    /// Controller responsible for managing menu items.
    /// Provides actions to view the menu and add new menu items.
    /// </summary>
    public class MenuController : Controller
    {
        private readonly ApplicationDbContext _context;

        /// <summary>
        /// Initializes a new instance of the <see cref="MenuController"/> class.
        /// </summary>
        /// <param name="context">The database context for menu items.</param>
        public MenuController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Displays all menu items in a card-style layout.
        /// </summary>
        /// <returns>The view containing the list of menu items.</returns>
        public IActionResult ViewMenu()
        {
            // Fetch all menu items from database and map Entity → ViewModel
            var items = _context.MenuItems
                .Select(entity => new MenuItem
                {
                    Name = entity.Name,
                    Description = entity.Description,
                    Category = entity.Category,
                    Price = entity.Price,
                    IsAvailable = entity.IsAvailable
                })
                .ToList();

            return View(items);
        }

        /// <summary>
        /// Displays the form to add a new menu item.
        /// </summary>
        /// <returns>The AddMenuItem view.</returns>
        [HttpGet]
        public IActionResult AddMenuItem()
        {
            return View();
        }

        /// <summary>
        /// Handles form submission for adding a new menu item.
        /// Maps the view model to a database entity and saves it.
        /// </summary>
        /// <param name="model">The <see cref="MenuItem"/> view model containing form data.</param>
        /// <returns>
        /// Redirects to the ViewMenu page if successful; otherwise, redisplays the form with validation errors.
        /// </returns>
        [HttpPost]
        public async Task<IActionResult> AddMenuItem(MenuItem model)
        {
            if (ModelState.IsValid)
            {
                // Map ViewModel → Entity
                var entity = new MenuItemEntity
                {
                    Name = model.Name,
                    Description = model.Description,
                    Category = model.Category,
                    Price = model.Price,
                    IsAvailable = model.IsAvailable
                };

                _context.MenuItems.Add(entity);
                await _context.SaveChangesAsync();

                return RedirectToAction("ViewMenu");
            }

            // Return form with validation errors
            return View(model);
        }
    }
}
