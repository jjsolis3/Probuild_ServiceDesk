using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class UsersController : Controller
    {
        // GET: Users
        public IActionResult Profile()
        {
            return View();
        }
        public IActionResult Settings()
        {
            return View();
        }
    }
}