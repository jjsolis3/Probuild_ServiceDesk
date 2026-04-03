using Microsoft.AspNetCore.Mvc;

namespace Jobick.Controllers
{
	public class AjaxController : Controller
	{
		public IActionResult FeaturedCompanies()
		{
			return View();
		}
		
		public IActionResult RecentActivity()
		{
			return View();
		}
	}
}
