using Microsoft.AspNetCore.Mvc;
using ProjectE.Models.Entities;
using ProjectE.Services;
using System.Collections.Generic;

namespace ProjectE.Controllers
{
    [ApiController]
    [Route("api/menu")]
    public class MenuApiController : ControllerBase
    {
        private readonly IMenuService _menuService;

        public MenuApiController(IMenuService menuService)
        {
            _menuService = menuService;
        }

        // GET: api/menu
        [HttpGet]
        public ActionResult<List<MenuItemEntity>> GetAll()
        {
            var items = _menuService.GetAllMenuItems();
            return Ok(items);
        }

        // GET: api/menu/{id}
        [HttpGet("{id}")]
        public ActionResult<MenuItemEntity> GetById(int id)
        {
            var item = _menuService.GetMenuItemById(id);
            if (item == null) return NotFound();
            return Ok(item);
        }

        // POST: api/menu
        [HttpPost]
        public ActionResult<MenuItemEntity> Add(MenuItemEntity item)
        {
            _menuService.AddMenuItem(item);
            return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
        }

        // PUT: api/menu/{id}
        [HttpPut("{id}")]
        public IActionResult Update(int id, MenuItemEntity item)
        {
            var existing = _menuService.GetMenuItemById(id);
            if (existing == null) return NotFound();

            item.Id = id;
            _menuService.UpdateMenuItem(item);
            return NoContent();
        }

        // DELETE: api/menu/{id}
        [HttpDelete("{id}")]
        public IActionResult Delete(int id)
        {
            var existing = _menuService.GetMenuItemById(id);
            if (existing == null) return NotFound();

            _menuService.DeleteMenuItem(id);
            return NoContent();
        }
    }
}
