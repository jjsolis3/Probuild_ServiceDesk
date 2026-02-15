using Microsoft.AspNetCore.Mvc;

namespace Sliced.Controllers
{
    public class AuthenticationController : Controller
    {
        // GET: Authentication
        public IActionResult Signin()
        {
            return View();
        }
        public IActionResult Signup()
        {
            return View();
        }
        public IActionResult ResetPw()
        {
            return View();
        }
    }
}