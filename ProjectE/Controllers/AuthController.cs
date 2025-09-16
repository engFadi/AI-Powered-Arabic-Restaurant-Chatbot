using Microsoft.AspNetCore.Mvc;
using ProjectE.Models.Auth;

namespace ProjectE.Controllers
{
    /// <summary>
    /// Controller responsible for handling user authentication actions.
    /// </summary>
    public class AuthController : Controller
    {
        [HttpGet]
        public IActionResult Login()
        {
            return View(new LoginViewModel());
        }

        [HttpGet]
        public IActionResult Register()
        {
            return View();
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login", "Auth");
        }
    }
}
