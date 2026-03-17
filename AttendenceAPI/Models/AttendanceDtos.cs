using System;
using System.Collections.Generic;

namespace AttendenceAPI.Models
{
    public class AttendanceEnvelope
    {
        public List<EntryAttendanceDto> entryAttendance { get; set; }
    }

    public class EntryAttendanceDto
    {
        public DateTime dateTime { get; set; } 
        public EmployeeDto employee { get; set; }
        public DeviceDto device { get; set; }
        public string registrationType { get; set; }
    }

    public class EmployeeDto
    {
        public long card_id { get; set; } 
    }

    public class DeviceDto
    {
        public int id { get; set; }
        public string code { get; set; }
        public string description { get; set; }
    }


}
