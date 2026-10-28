using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

string connectionString = config.GetConnectionString("Gts")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:Gts en appsettings.json");
string sutranUrl = config["Sutran:Url"]
    ?? throw new InvalidOperationException("Falta Sutran:Url en appsettings.json");
string accessToken = config["Sutran:AccessToken"]
    ?? throw new InvalidOperationException("Falta Sutran:AccessToken en appsettings.json");
int cycleSeconds = int.TryParse(config["Sutran:CycleSeconds"], out int cs) && cs > 0 ? cs : 60;

var lastSent = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
};
using var http = new HttpClient(handler);
http.DefaultRequestHeaders.Add("access-token", accessToken);

Console.WriteLine($"Agente SUTRAN iniciado — ciclo cada {cycleSeconds}s");
Console.WriteLine($"URL: {sutranUrl}");
Console.WriteLine(new string('─', 60));

while (true)
{
    try
    {
        List<DeviceRecord> devices = QueryDevices();
        Console.WriteLine($"[{Now()}] Dispositivos sutran=1: {devices.Count}");

        foreach (DeviceRecord d in devices)
        {
            if (!lastSent.TryGetValue(d.DeviceId, out long prev) || prev != d.GpsTimestamp)
            {
                await SendAsync(d);
                lastSent[d.DeviceId] = d.GpsTimestamp;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{Now()}] ERROR ciclo: {ex.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(cycleSeconds));
}

static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

List<DeviceRecord> QueryDevices()
{
    var list = new List<DeviceRecord>();
    using var conn = new MySqlConnection(connectionString);
    conn.Open();
    using var cmd = new MySqlCommand(@"
        SELECT accountID, deviceID, lastValidLatitude, lastValidLongitude,
               lastValidHeading, lastValidSpeed, lastGPSTimestamp, uniqueID
        FROM device
        WHERE sutran = '1'
          AND lastGPSTimestamp >= UNIX_TIMESTAMP(NOW() - INTERVAL 20 DAY)", conn);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        list.Add(new DeviceRecord(
            reader.IsDBNull(reader.GetOrdinal("accountID")) ? "" : reader.GetString("accountID"),
            reader.GetString("deviceID"),
            reader.GetDouble("lastValidLatitude"),
            reader.GetDouble("lastValidLongitude"),
            reader.GetDouble("lastValidHeading"),
            reader.GetDouble("lastValidSpeed"),
            reader.GetInt64("lastGPSTimestamp"),
            reader.IsDBNull(reader.GetOrdinal("uniqueID")) ? "" : reader.GetString("uniqueID")));
    }
    return list;
}

void SaveAuditoria(string accountId, string deviceId, string envio, string respuesta)
{
    try
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();
        using var cmd = new MySqlCommand(@"
            INSERT INTO auditoriasutran (accountID, deviceID, fecharegistro, lastenvio, lastrespuesta)
            VALUES (@accountID, @deviceID, NOW(), @envio, @respuesta)", conn);
        cmd.Parameters.AddWithValue("@accountID", accountId);
        cmd.Parameters.AddWithValue("@deviceID", deviceId);
        cmd.Parameters.AddWithValue("@envio", envio);
        cmd.Parameters.AddWithValue("@respuesta", respuesta);
        cmd.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        Console.WriteLine("  !! Auditoría error: " + ex.Message);
    }
}

async Task SendAsync(DeviceRecord deviceRecord)
{
    object payload = BuildPayload(deviceRecord);
    string json = JsonSerializer.Serialize(payload);
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    Console.WriteLine($"  >> {deviceRecord.DeviceId}  {json}");

    string respuesta;
    try
    {
        HttpResponseMessage resp = await http.PostAsync(sutranUrl, content);
        respuesta = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"  << [{(int)resp.StatusCode}] {respuesta}");
    }
    catch (Exception ex)
    {
        respuesta = "HTTP_ERROR: " + ex.Message;
        Console.WriteLine("  !! " + respuesta);
    }

    SaveAuditoria(deviceRecord.AccountId, deviceRecord.DeviceId, json, respuesta);
}

static object BuildPayload(DeviceRecord deviceRecord)
{
    string plate = TransformPlate(deviceRecord.DeviceId);
    int speedInt = (int)Math.Round(deviceRecord.Speed);

    var payload = new Dictionary<string, object>
    {
        ["plate"] = plate,
        ["geo"] = new double[] { deviceRecord.Latitude, deviceRecord.Longitude },
        ["direction"] = (int)Math.Round(deviceRecord.Heading) % 360,
        ["event"] = speedInt > 0 ? "ER" : "PA",
        ["speed"] = speedInt,
        ["time_device"] = UnixToGmt5String(deviceRecord.GpsTimestamp)
    };

    string imei = TransformImei(deviceRecord.UniqueId);
    if (!string.IsNullOrEmpty(imei) && long.TryParse(imei, out long imeiLong))
    {
        payload["imei"] = imeiLong;
    }

    return payload;
}

static string TransformImei(string uniqueId)
{
    if (string.IsNullOrEmpty(uniqueId))
    {
        return "";
    }

    string digits = new string(uniqueId.Where(char.IsDigit).ToArray());
    if (digits.StartsWith('0') && digits.Length > 15)
    {
        digits = digits.TrimStart('0');
    }

    return digits.Length != 15 ? "" : digits;
}

static string TransformPlate(string deviceId)
{
    string clean = deviceId.Replace("-", "");
    return clean.Length < 6 ? clean : clean.Substring(clean.Length - 6);
}

static string UnixToGmt5String(long unixSec)
{
    return DateTimeOffset.FromUnixTimeSeconds(unixSec).UtcDateTime.AddHours(-5).ToString("yyyy-MM-dd HH:mm:ss");
}
