using Microsoft.AspNetCore.Mvc;

namespace ProjectE.Controllers
{
    public class AdminPageController : Controller // renamed from AdminPagController
    {
        public IActionResult Cover()
        {
            return View(); // Views/AdminPage/Cover.cshtml
        }
    }
}
