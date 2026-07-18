using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace ExcelSearch___CB.Controllers
{
    public class UserDashboardController : Controller
    {
        // GET: UserDashboard
        public ActionResult Index()
        {
            return View();
        }


        public ActionResult Search()
        {
            return View();
        }

        public ActionResult FilterBuilder()
        {
            return View();
        }
    }
}