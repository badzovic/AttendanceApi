using System;
using System.Web.Http;

[RoutePrefix("api")]
public class PingController : ApiController
{
    // GET /api/ping
    [HttpGet]
    [Route("ping")]
    public IHttpActionResult Ping()
    {
        return Ok(new
        {
            ok = true,
            utc = DateTime.UtcNow
        });
    }
}