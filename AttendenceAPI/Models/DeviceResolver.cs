using System;
using System.Configuration;

public static class DeviceResolver
{
    public static ResolvedDevice Resolve(string deviceCode)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
            return null;

        var key = $"device:{deviceCode}";
        var raw = ConfigurationManager.AppSettings[key];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split('|');
        if (parts.Length < 3)
            return null;

        return new ResolvedDevice
        {
            Id = int.Parse(parts[0]),
            Code = parts[1],
            Description = parts[2]
        };
    }
}

public class ResolvedDevice
{
    public int Id { get; set; }
    public string Code { get; set; }
    public string Description { get; set; }
}
