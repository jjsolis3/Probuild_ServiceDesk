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
    }
}