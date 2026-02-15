using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class ChartController : Controller
    {
        // GET: Chart
        public IActionResult Index()
        {
            return View();
        }
    }
}