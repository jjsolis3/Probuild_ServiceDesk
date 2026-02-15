using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class DragAndDropController : Controller
    {
        // GET: DragAndDrop
        public IActionResult Index()
        {
            return View();
        }
    }
}