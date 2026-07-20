using Microsoft.AspNetCore.Identity;

namespace ExcelSearch___CB.Data
{
    public class AppUser : IdentityUser
    {
        public string FullName { get; set; }
    }
}
