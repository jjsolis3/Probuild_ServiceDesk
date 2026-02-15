using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class TablesController : Controller
    {
        // GET: Tables
        public IActionResult Basic()
        {
            return View();
        }
        public IActionResult Datatables()
        {
            return View();
        }
        public IActionResult Editable()
        {
            return View();
        }
    }
}