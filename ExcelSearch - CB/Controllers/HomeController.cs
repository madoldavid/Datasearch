using ExcelSearch___CB.Data;
using ExcelSearch___CB.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace ExcelSearch___CB.Controllers
{
    public class HomeController : Controller
    {
        private readonly UserManager<AppUser> _userManager;
        private readonly SignInManager<AppUser> _signInManager;

        public HomeController(
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager)
        {
            _userManager = userManager;
            _signInManager = signInManager;
        }

        public IActionResult Index()
        {
            if (User.Identity.IsAuthenticated)
                return RedirectToAction("Index", "UserDashboard");
            return View();
        }

        public IActionResult About()
        {
            ViewBag.Message = "Your application description page.";
            return View();
        }

        public IActionResult Contact()
        {
            ViewBag.Message = "Your contact page.";
            return View();
        }

        public IActionResult Login()
        {
            if (User.Identity.IsAuthenticated)
                return RedirectToAction("Index", "UserDashboard");
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var result = await _signInManager.PasswordSignInAsync(
                model.Username, model.Password, model.RememberMe, lockoutOnFailure: false);

            if (result.Succeeded)
            {
                return RedirectToAction("Index", "UserDashboard");
            }

            ModelState.AddModelError("", "Invalid username or password.");
            return View(model);
        }

        public IActionResult Signup()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Signup(AdminSignupViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            if (model.Password != model.ConfirmPassword)
            {
                ModelState.AddModelError("ConfirmPassword", "Passwords do not match.");
                return View(model);
            }

            var existing = await _userManager.FindByNameAsync(model.Username);
            if (existing != null)
            {
                ModelState.AddModelError("Username", "Username already exists.");
                return View(model);
            }

            var user = new AppUser
            {
                UserName = model.Username,
                FullName = model.FullName
            };

            var result = await _userManager.CreateAsync(user, model.Password);

            if (result.Succeeded)
            {
                // Auto-assign User role
                await _userManager.AddToRoleAsync(user, "User");
                TempData["Message"] = "Account created successfully. You can now log in.";
                return RedirectToAction("Login");
            }

            foreach (var error in result.Errors)
                ModelState.AddModelError("", error.Description);

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Index");
        }

        [AllowAnonymous]
        public IActionResult Error()
        {
            return View();
        }
    }
}
