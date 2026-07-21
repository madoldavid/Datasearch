using ExcelSearch___CB.Data;
using ExcelSearch___CB.Models;
using ExcelSearch___CB.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;

namespace ExcelSearch___CB.Controllers
{
    public class HomeController : Controller
    {
        private readonly UserManager<AppUser> _userManager;
        private readonly SignInManager<AppUser> _signInManager;
        private readonly IConfiguration _config;
        private readonly ConfigurationService _configService;

        public HomeController(
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            IConfiguration config,
            ConfigurationService configService)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _config = config;
            _configService = configService;
        }

        public async Task<IActionResult> Index()
        {
            if (User.Identity.IsAuthenticated)
                return RedirectToAction("Index", "UserDashboard");
            
            var config = await _configService.GetAppConfig();
            var landingStrings = await _configService.GetStringsByPage("Index");
            
            ViewBag.AppConfig = config;
            ViewBag.UIStrings = landingStrings;
            
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

        public async Task<IActionResult> Login()
        {
            if (User.Identity.IsAuthenticated)
                return RedirectToAction("Index", "UserDashboard");
            
            var config = await _configService.GetAppConfig();
            var loginStrings = await _configService.GetStringsByPage("Login");
            
            ViewBag.AppConfig = config;
            ViewBag.UIStrings = loginStrings;
            
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
                var user = await _userManager.FindByNameAsync(model.Username);
                var roles = await _userManager.GetRolesAsync(user!);
                var adminRole = _config.GetValue("RoleNames:Admin", "Admin");

                if (roles.Contains(adminRole))
                {
                    return RedirectToAction("Overview", "Admin");
                }
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
                // Auto-assign default user role from config
                var defaultRole = _config.GetValue("RoleNames:User", "User")!;
                await _userManager.AddToRoleAsync(user, defaultRole);
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
