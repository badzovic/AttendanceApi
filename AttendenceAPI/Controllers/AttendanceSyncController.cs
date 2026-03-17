using System;
using System.Linq;
using System.Web.Http;
using DataAccess;

[RoutePrefix("api/attendance")]
public class AttendanceSyncController : ApiController
{
    [HttpGet]
    [Route("for-sync/{lastId:int}")]
    public IHttpActionResult ForSync(int lastId)
    {
        using (var db = new HRMEntities())
        {
            // Učitaj nove zapise (inkrementalno)
            var rows = db.AttendanceEvents
                .Where(x => x.Id > lastId)
                .OrderBy(x => x.Id)
                .Take(500) // zaštita
                .ToList();

            if (!rows.Any())
            {
                return Ok(new
                {
                    lastId = lastId,
                    entryAttendanceList = new object[0]
                });
            }

            var valid = rows.Where(x => x.Hr_korisnik_id.HasValue).ToList();

            var result = valid.Select(x => new
            {
                id = x.Id,
                dateTime = x.DateTimeUtc, // UTC

                employee = new
                {
                    reference = x.Hr_korisnik_id.Value.ToString(),

                    id = (int?)null,
                    number = (string)null,
                    givenName = (string)null,
                    familyName = (string)null,
                    fullName = (string)null,
                    controllers = new object[0],
                    supervisors = new object[0]
                },

                device = new
                {
                    id = x.DeviceId,
                    code = x.DeviceCode,
                    description = x.DeviceDescription
                },

                registrationType = x.RegistrationType
            }).ToList();

            // lastId mora biti zadnji viđeni ID iz ORIGINALNOG seta (da ne zapne ako su neki null)
            var newLastId = rows.Max(x => x.Id);

            return Ok(new
            {
                lastId = newLastId,
                entryAttendanceList = result
            });
        }
    }
}
