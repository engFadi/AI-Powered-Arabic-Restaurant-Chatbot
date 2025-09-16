using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ProjectE.Controllers
{
   // [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        public IActionResult TrackOrder()
        {
            return View();
        }
    }
}
