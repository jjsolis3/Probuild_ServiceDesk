using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class AppsController : Controller
    {
        // GET: Apps
        public IActionResult Email()
        {
            return View();
        }
        public IActionResult Chat()
        {
            return View();
        }
        public IActionResult Contact()
        {
            return View();
        }
        public IActionResult Invoice()
        {
            return View();
        }
        public IActionResult Calender()
        {
            return View();
        }

    }
}