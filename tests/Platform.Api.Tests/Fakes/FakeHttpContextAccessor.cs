using Microsoft.AspNetCore.Http;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}
