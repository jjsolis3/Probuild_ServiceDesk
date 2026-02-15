using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class PagesController : Controller
    {
        // GET: Pages
        public IActionResult StarterPage()
        {
            return View();
        }
        public IActionResult Maintenance()
        {
            return View();
        }
        public IActionResult ComingSoon()
        {
            return View();
        }
        public IActionResult Error404()
        {
            return View();
        }
        public IActionResult Error500()
        {
            return View();
        }
        public IActionResult Error503()
        {
            return View();
        }
    }
}