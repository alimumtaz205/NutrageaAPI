using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nutragea.Api.Contracts;
using Nutragea.Api.Security;

namespace Nutragea.Commerce.Controllers;
[ApiController, Route("capi/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    public Task<object> Login(LoginRequest r, CancellationToken ct) => auth.Login(r, ct);
    [HttpPost("logout")]
    public async Task<object> Logout(CancellationToken ct)
    {
        await auth.Logout(long.Parse(User.FindFirst("sub")!.Value), User.FindFirst("sid")!.Value, ct);
        return new
        {
            message = "Signed out"
        };
    }

    [HttpGet("me")]
    public object Me() => new
    {
        id = User.FindFirst("sub")!.Value,
        username = User.FindFirst("name")!.Value,
        role = User.FindFirst("role")!.Value
    };
}
