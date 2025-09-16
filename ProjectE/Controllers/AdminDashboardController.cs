using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// [Authorize(Roles = "Admin,admin")]
/// </summary>
public class AdminDashboardController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
