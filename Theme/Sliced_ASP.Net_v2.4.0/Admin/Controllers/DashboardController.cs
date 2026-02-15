using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class DashboardController : Controller
    {
        // GET: Dashboard
        public IActionResult Index()
        {
            return View();
        }
        public IActionResult Project()
        {
            return View();
        }
        public IActionResult Ecommerce()
        {
            return View();
        }
    }
}