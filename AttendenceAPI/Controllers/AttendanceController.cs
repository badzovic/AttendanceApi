using DataAccess;
using System;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Web.Http;
using AttendenceAPI.Models;
using System.Collections.Generic;

[RoutePrefix("api/attendance")]
public class AttendanceController : ApiController
{
    private static readonly TimeZoneInfo BosniaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");


    [HttpPost]
    [Route("entry")]
    public IHttpActionResult Entry([FromBody] AttendanceEnvelope model)
    {
        if (model?.entryAttendance == null || !model.entryAttendance.Any())
            return BadRequest("entryAttendance is empty");

        var e = model.entryAttendance.First();

        var reg = (e.registrationType ?? "").Trim().ToUpperInvariant();
        if (!new[] { "CLOCK_IN", "CLOCK_OUT", "PRIVATE_OUT", "BUSINESS_OUT" }.Contains(reg))
            return BadRequest("Invalid registrationType");

        var dtUtc = e.dateTime.Kind == DateTimeKind.Utc
            ? e.dateTime
            : DateTime.SpecifyKind(e.dateTime, DateTimeKind.Utc);

        var resolvedDevice = DeviceResolver.Resolve(e.device.code);
        if (resolvedDevice == null)
        {
            return Ok(new
            {
                success = false,
                id = 0,
                deviceCode = e.device.code,
                message = "Dogodila se greška!"
            });
        }

        try
        {
            using (var db = new HRMEntities())
            {
                var cardStr = e.employee.card_id.ToString();

                var user = db.HR_KORISNIK
                    .FirstOrDefault(x => x.PACS_ID != null && x.PACS_ID.Trim() == cardStr);

                if (user == null)
                {
                    return Ok(new
                    {
                        success = false,
                        id = 0,
                        deviceCode = e.device.code,
                        message = "Dogodila se greška!"
                    });
                }

                var lokalnoVrijeme = TimeZoneInfo.ConvertTimeFromUtc(dtUtc, BosniaTimeZone);
                var datum = lokalnoVrijeme.Date;

                var imaAttendoZaDanas = HasAnyAttendoEntryForDay(db, user.ID, datum);

                if (!imaAttendoZaDanas && reg != "CLOCK_IN")
                {
                    return Ok(new
                    {
                        success = false,
                        id = 0,
                        deviceCode = e.device.code,
                        message = "Prva evidencija mora biti DOLAZAK!"
                    });
                }

                // ===============================
                // 1) RAW EVENT INSERT
                // ===============================
                var entity = new AttendanceEvents
                {
                    DateTimeUtc = dtUtc,
                    CreatedAtUtc = DateTime.UtcNow,
                    CardNumber = e.employee.card_id,
                    DeviceId = resolvedDevice.Id,
                    DeviceCode = resolvedDevice.Code,
                    DeviceDescription = resolvedDevice.Description,
                    RegistrationType = reg,
                    Hr_korisnik_id = user.ID,
                    Source = "API"
                };

                db.AttendanceEvents.Add(entity);
                db.SaveChanges();

                // ===============================
                // 2) PRISUSTVO LOGIKA 
                // ===============================
                try
                {
                    if (reg == "CLOCK_IN" && IsFirstApiClockInOfDay(db, user.ID, datum))
                    {
                        ObrisiPostojecePrisustvo(db, user.ID, datum);
                    }

                    var isAdministrativno = (user.ADMIINISTRATIVNO ?? "").Trim().ToUpper() == "Y";
                    ApplyToPrisustvo(db, user.ID, lokalnoVrijeme, reg, isAdministrativno);
                    db.SaveChanges();

                    if (reg == "CLOCK_OUT" && IsPauseEnabled())
                    {
                        InsertPauseIfNeeded(db, user.ID, datum);
                        db.SaveChanges();
                    }
                   
                }
                catch (Exception ex2)
                {
                    
                }

                // ===============================
                // SUCCESS ZA UREĐAJ
                // ===============================
                return Ok(new
                {
                    success = true,
                    id = entity.Id,
                    deviceCode = e.device.code,
                    message = $"Hvala, {user.IME} {user.PREZIME}!"
                });
            }
        }
        catch (Exception ex)
        {

            return Ok(new
            {
                success = false,
                id = 0,
                deviceCode = e.device.code,
                message = "Dogodila se greška!"
            });
        }
    }

    private static bool IsPauseEnabled()
    {
        return bool.TryParse(ConfigurationManager.AppSettings["EnablePauseLogic"], out var enabled) && enabled;
    }

    private static bool IsFirstApiClockInOfDay(HRMEntities db, int korisnikId, DateTime datum)
    {
        var datumOd = datum.Date;
        var datumDo = datumOd.AddDays(1);

        var postojiAttendoPrisustvo = db.HR_KORISNIK_PRISUSTVO.Any(x =>
            x.HR_KORISNIK_ID == korisnikId &&
            x.VRIJEME_OD >= datumOd &&
            x.VRIJEME_OD < datumDo &&
            x.ISATTENDO == "Y" &&
            (x.OBRISANO == null || x.OBRISANO != "Y"));

        return !postojiAttendoPrisustvo;
    }

   
    private static void ApplyToPrisustvo(HRMEntities db, int korisnikId, DateTime lokalnoVrijeme, string reg, bool isAdministrativno)
    {
        var datum = lokalnoVrijeme.Date;
        var datumOd = datum;
        var datumDo = datum.AddDays(1);

        var otvoreniRed = db.HR_KORISNIK_PRISUSTVO
            .Where(x => x.HR_KORISNIK_ID == korisnikId
                     && x.VRIJEME_OD >= datumOd
                     && x.VRIJEME_OD < datumDo
                     && x.VRIJEME_DO == null
                     && (x.OBRISANO == null || x.OBRISANO != "Y")
                     && x.ISATTENDO == "Y")
            .OrderByDescending(x => x.VRIJEME_OD)
            .FirstOrDefault();

        switch (reg)
        {
            case "CLOCK_IN":
                // Ako postoji otvoreni izlaz, zatvori ga
                if (otvoreniRed != null &&
                    (otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 10 || otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 23))
                {
                    otvoreniRed.VRIJEME_DO = lokalnoVrijeme;
                    otvoreniRed.DATUM_IZMJENE = DateTime.Now;
                }

                // Ako već postoji otvoreni rad, ništa ne radi
                var postojiOtvorenRad = db.HR_KORISNIK_PRISUSTVO.Any(x =>
                    x.HR_KORISNIK_ID == korisnikId &&
                    x.VRIJEME_OD >= datumOd &&
                    x.VRIJEME_OD < datumDo &&
                    x.VRIJEME_DO == null &&
                    x.VRSTA_PRISUSTVA_ODSUSTVA_ID == 1 &&
                    (x.OBRISANO == null || x.OBRISANO != "Y") &&
                    x.ISATTENDO == "Y");

                if (!postojiOtvorenRad)
                {
                    // ADMINISTRATIVNO kašnjenje:
                    // ako je prvi dolazak tog dana poslije 08:30, upišemo privatni izlaz od 08:30 do vremena prijave
                    if (isAdministrativno)
                    {
                        var granicaAdministrativno = new DateTime(datum.Year, datum.Month, datum.Day, 8, 30, 0);

                        var postojiBiloStaTajDan = db.HR_KORISNIK_PRISUSTVO.Any(x =>
                            x.HR_KORISNIK_ID == korisnikId &&
                            x.VRIJEME_OD >= datumOd &&
                            x.VRIJEME_OD < datumDo &&
                            (x.OBRISANO == null || x.OBRISANO != "Y") &&
                            x.ISATTENDO == "Y");

                        if (!postojiBiloStaTajDan && lokalnoVrijeme > granicaAdministrativno)
                        {
                            db.HR_KORISNIK_PRISUSTVO.Add(new HR_KORISNIK_PRISUSTVO
                            {
                                HR_KORISNIK_ID = korisnikId,
                                VRSTA_PRISUSTVA_ODSUSTVA_ID = 10, // Privatni izlaz
                                VRIJEME_OD = granicaAdministrativno,
                                VRIJEME_DO = lokalnoVrijeme,
                                REFERENT_ID = 1168,
                                ISATTENDO = "Y",
                                DATUM_KREIRANJA = DateTime.Now
                            });
                        }
                    }

                    // Otvori redovan rad
                    db.HR_KORISNIK_PRISUSTVO.Add(new HR_KORISNIK_PRISUSTVO
                    {
                        HR_KORISNIK_ID = korisnikId,
                        VRSTA_PRISUSTVA_ODSUSTVA_ID = 1,
                        VRIJEME_OD = lokalnoVrijeme,
                        VRIJEME_DO = null,
                        REFERENT_ID = 1168,
                        ISATTENDO = "Y",
                        DATUM_KREIRANJA = DateTime.Now
                    });
                }
                break;

            case "PRIVATE_OUT":
                // Ako već postoji otvoreni izlaz, ne otvaraj novi
                if (otvoreniRed != null &&
                    (otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 10 || otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 23))
                {
                    return;
                }

                // Zatvori otvoreni rad
                if (otvoreniRed != null && otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 1)
                {
                    otvoreniRed.VRIJEME_DO = lokalnoVrijeme;
                    otvoreniRed.DATUM_IZMJENE = DateTime.Now;
                }

                // Otvori privatni izlaz
                db.HR_KORISNIK_PRISUSTVO.Add(new HR_KORISNIK_PRISUSTVO
                {
                    HR_KORISNIK_ID = korisnikId,
                    VRSTA_PRISUSTVA_ODSUSTVA_ID = 10,
                    VRIJEME_OD = lokalnoVrijeme,
                    VRIJEME_DO = null,
                    REFERENT_ID = 1168,
                    ISATTENDO = "Y",
                    DATUM_KREIRANJA = DateTime.Now
                });
                break;

            case "BUSINESS_OUT":
                // Ako već postoji otvoreni izlaz, ne otvaraj novi
                if (otvoreniRed != null &&
                    (otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 10 || otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 23))
                {
                    return;
                }

                // Zatvori otvoreni rad
                if (otvoreniRed != null && otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 1)
                {
                    otvoreniRed.VRIJEME_DO = lokalnoVrijeme;
                    otvoreniRed.DATUM_IZMJENE = DateTime.Now;
                }

                // Otvori službeni izlaz
                db.HR_KORISNIK_PRISUSTVO.Add(new HR_KORISNIK_PRISUSTVO
                {
                    HR_KORISNIK_ID = korisnikId,
                    VRSTA_PRISUSTVA_ODSUSTVA_ID = 23,
                    VRIJEME_OD = lokalnoVrijeme,
                    VRIJEME_DO = null,
                    REFERENT_ID = 1168,
                    ISATTENDO = "Y",
                    DATUM_KREIRANJA = DateTime.Now
                });
                break;

            case "CLOCK_OUT":
                // Ako je otvoren rad, zatvori ga
                if (otvoreniRed != null && otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 1)
                {
                    otvoreniRed.VRIJEME_DO = lokalnoVrijeme;
                    otvoreniRed.DATUM_IZMJENE = DateTime.Now;
                }

                // Ako je otvoren izlaz, zatvori i njega na CLOCK_OUT
                if (otvoreniRed != null &&
                    (otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 10 || otvoreniRed.VRSTA_PRISUSTVA_ODSUSTVA_ID == 23))
                {
                    otvoreniRed.VRIJEME_DO = lokalnoVrijeme;
                    otvoreniRed.DATUM_IZMJENE = DateTime.Now;
                }               

                break;
        }
    }
    private static bool HasAnyAttendoEntryForDay(HRMEntities db, int korisnikId, DateTime datum)
    {
        var datumOd = datum.Date;
        var datumDo = datumOd.AddDays(1);

        return db.HR_KORISNIK_PRISUSTVO.Any(x =>
            x.HR_KORISNIK_ID == korisnikId &&
            x.VRIJEME_OD >= datumOd &&
            x.VRIJEME_OD < datumDo &&
            x.ISATTENDO == "Y" &&
            (x.OBRISANO == null || x.OBRISANO != "Y"));
    }
    private static void InsertPauseIfNeeded(HRMEntities db, int korisnikId, DateTime datum)
    {
        var datumOd = datum.Date;
        var datumDo = datumOd.AddDays(1);
        var pauzaTrajanje = TimeSpan.FromMinutes(30);
        var minimalnoRada = TimeSpan.FromMinutes(90);

        // Ako pauza već postoji - ne radi ništa
        var vecPostojiPauza = db.HR_KORISNIK_PRISUSTVO.Any(x =>
            x.HR_KORISNIK_ID == korisnikId &&
            x.VRIJEME_OD >= datumOd &&
            x.VRIJEME_OD < datumDo &&
            x.VRSTA_PRISUSTVA_ODSUSTVA_ID == 24 &&
            (x.OBRISANO == null || x.OBRISANO != "Y") &&
            x.ISATTENDO == "Y");

        if (vecPostojiPauza)
            return;

        // Radni blokovi završeni tog dana
        var radniBlokovi = db.HR_KORISNIK_PRISUSTVO
            .Where(x => x.HR_KORISNIK_ID == korisnikId
                     && x.VRIJEME_OD >= datumOd
                     && x.VRIJEME_OD < datumDo
                     && x.VRSTA_PRISUSTVA_ODSUSTVA_ID == 1
                     && x.VRIJEME_DO != null
                     && (x.OBRISANO == null || x.OBRISANO != "Y")
                     && x.ISATTENDO == "Y")
            .OrderBy(x => x.VRIJEME_OD)
            .ToList();

        if (!radniBlokovi.Any())
            return;

      
        var ukupnoRada = radniBlokovi.Sum(x => (x.VRIJEME_DO.Value - x.VRIJEME_OD).TotalMinutes);

        if (ukupnoRada < minimalnoRada.TotalMinutes)
            return;
        var pocetakDana = radniBlokovi.Min(x => x.VRIJEME_OD);
        var krajDana = radniBlokovi.Max(x => x.VRIJEME_DO.Value);

        // Cilj pauze = sredina radnog raspona
        var target = pocetakDana.AddMinutes((krajDana - pocetakDana).TotalMinutes / 2.0);

        // Kandidati = radni blokovi koji mogu primiti 30 min
        var kandidati = radniBlokovi
            .Where(x => (x.VRIJEME_DO.Value - x.VRIJEME_OD) >= pauzaTrajanje)
            .Select(x => new
            {
                Blok = x,
                SredinaBloka = x.VRIJEME_OD.AddMinutes((x.VRIJEME_DO.Value - x.VRIJEME_OD).TotalMinutes / 2.0)
            })
            .OrderBy(x => Math.Abs((x.SredinaBloka - target).TotalMinutes))
            .ToList();

        foreach (var kandidat in kandidati)
        {
            var blok = kandidat.Blok;
            var originalEnd = blok.VRIJEME_DO.Value;
            var blockDuration = originalEnd - blok.VRIJEME_OD;

            // pauzu centriraj unutar tog bloka
            var pauseStart = blok.VRIJEME_OD.AddMinutes((blockDuration.TotalMinutes - 30) / 2.0);
            var pauseEnd = pauseStart.AddMinutes(30);

            // sigurnosno ostani unutar bloka
            if (pauseStart < blok.VRIJEME_OD)
                pauseStart = blok.VRIJEME_OD;

            if (pauseEnd > originalEnd)
                pauseEnd = originalEnd;

            if ((pauseEnd - pauseStart).TotalMinutes < 30)
                continue;

            // skrati originalni radni blok
            blok.VRIJEME_DO = pauseStart;
            blok.DATUM_IZMJENE = DateTime.Now;

            // ubaci pauzu
            db.HR_KORISNIK_PRISUSTVO.Add(new HR_KORISNIK_PRISUSTVO
            {
                HR_KORISNIK_ID = korisnikId,
                VRSTA_PRISUSTVA_ODSUSTVA_ID = 24,
                VRIJEME_OD = pauseStart,
                VRIJEME_DO = pauseEnd,
                REFERENT_ID = 1168,
                ISATTENDO = "Y",
                DATUM_KREIRANJA = DateTime.Now
            });

            // nastavak rada
            db.HR_KORISNIK_PRISUSTVO.Add(new HR_KORISNIK_PRISUSTVO
            {
                HR_KORISNIK_ID = korisnikId,
                VRSTA_PRISUSTVA_ODSUSTVA_ID = 1,
                VRIJEME_OD = pauseEnd,
                VRIJEME_DO = originalEnd,
                REFERENT_ID = 1168,
                ISATTENDO = "Y",
                DATUM_KREIRANJA = DateTime.Now
            });

            return;
        }
    }

    private static void ObrisiPostojecePrisustvo(HRMEntities db, int korisnikId, DateTime datum)
    {

        var datumOd = datum.Date;
        var datumDo = datumOd.AddDays(1);

        // vrati stare Attendo redove ako postoji ručni HRM unos
        db.Database.ExecuteSqlCommand(@"
        UPDATE HR_KORISNIK_PRISUSTVO
        SET OBRISANO = NULL,
            DATUM_BRISANJA = NULL,
            DATUM_IZMJENE = GETDATE()
        WHERE HR_KORISNIK_ID = @p0
          AND VRIJEME_OD >= @p1
          AND VRIJEME_OD < @p2
          AND VRSTA_PRISUSTVA_ODSUSTVA_ID = 1
          AND REFERENT_ID = 1168
          AND ISATTENDO = 'Y'
          AND OBRISANO = 'Y'
          AND EXISTS (
              SELECT 1
              FROM HR_KORISNIK_PRISUSTVO h2
              WHERE h2.HR_KORISNIK_ID = HR_KORISNIK_PRISUSTVO.HR_KORISNIK_ID
                AND h2.VRIJEME_OD >= @p1
                AND h2.VRIJEME_OD < @p2
                AND h2.VRSTA_PRISUSTVA_ODSUSTVA_ID = 1
                AND h2.REFERENT_ID <> 1168
                AND h2.ISHRM = 'Y'
                AND (h2.OBRISANO IS NULL OR h2.OBRISANO <> 'Y')
          )",
            korisnikId, datumOd, datumDo);

        // obriši ručni HRM unos za taj dan
        db.Database.ExecuteSqlCommand(@"
        UPDATE HR_KORISNIK_PRISUSTVO
        SET OBRISANO = 'Y',
            DATUM_BRISANJA = GETDATE()
        WHERE HR_KORISNIK_ID = @p0
          AND VRIJEME_OD >= @p1
          AND VRIJEME_OD < @p2
          AND ISHRM = 'Y'
          AND ISATTENDO IS NULL
          AND (OBRISANO IS NULL OR OBRISANO <> 'Y')",
            korisnikId, datumOd, datumDo);

        // resetuj Attendo redovan rad
        db.Database.ExecuteSqlCommand(@"
        UPDATE HR_KORISNIK_PRISUSTVO
        SET VRIJEME_DO = NULL,
            DATUM_IZMJENE = GETDATE()
        WHERE HR_KORISNIK_ID = @p0
          AND VRIJEME_OD >= @p1
          AND VRIJEME_OD < @p2
          AND ISHRM = 'Y'
          AND ISATTENDO = 'Y'
          AND VRSTA_PRISUSTVA_ODSUSTVA_ID = 1",
            korisnikId, datumOd, datumDo);
    }

    
}