// Network-isolation probe (dotnet_web stack). TCP connect attempts only, short timeouts.
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

var named = new (string Label, string Host, int Port)[] {
  ("host bridge gw 10.42.0.1:5830 (daemon)", "10.42.0.1", 5830),
  ("host bridge gw 10.42.0.1:22 (ssh)", "10.42.0.1", 22),
  ("host bridge gw 10.42.0.1:1433 (sql)", "10.42.0.1", 1433),
  ("host bridge gw 10.42.0.1:53 (dns)", "10.42.0.1", 53),
  ("host LAN ip 172.24.18.62:5830 (daemon)", "172.24.18.62", 5830),
  ("host LAN ip 172.24.18.62:22 (ssh)", "172.24.18.62", 22),
  ("portal 172.24.18.200:5800", "172.24.18.200", 5800),
  ("portal 172.24.18.200:445 (smb)", "172.24.18.200", 445),
  ("edge 172.24.18.206:443", "172.24.18.206", 443),
  ("edge/sql 172.24.18.206:1433", "172.24.18.206", 1433),
  ("router 172.24.18.1:443", "172.24.18.1", 443),
  ("dc 172.24.18.204:389 (ldap)", "172.24.18.204", 389),
  ("sydney 172.24.28.11:443", "172.24.28.11", 443),
  ("internet 1.1.1.1:443", "1.1.1.1", 443),
  ("internet github.com:443", "github.com", 443),
};

static async Task<(string Result, long Ms)> Tcp(string host, int port, int timeoutMs) {
  var sw = System.Diagnostics.Stopwatch.StartNew();
  using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
  try {
    using var cts = new CancellationTokenSource(timeoutMs);
    await s.ConnectAsync(host, port, cts.Token);
    return ("OPEN", sw.ElapsedMilliseconds);
  } catch (OperationCanceledException) { return ("timeout", sw.ElapsedMilliseconds); }
  catch (SocketException e) { return (e.SocketErrorCode.ToString(), sw.ElapsedMilliseconds); }
  catch (Exception e) { return (e.GetType().Name, sw.ElapsedMilliseconds); }
}

async Task<object> Run() {
  var results = new List<object>();
  foreach (var (label, host, port) in named) { var (r, ms) = await Tcp(host, port, 2500); results.Add(new { label, result = r, ms }); }
  string dns; try { dns = string.Join(",", (await Dns.GetHostAddressesAsync("github.com")).Select(a => a.ToString())); } catch (Exception e) { dns = e.Message; }
  var sib = new List<(string, int)>(); for (int h = 10; h <= 249; h++) foreach (var p in new[] { 8080, 5000, 3000 }) sib.Add(($"10.42.0.{h}", p));
  var sem = new SemaphoreSlim(64);
  var sibRes = await Task.WhenAll(sib.Select(async hp => { await sem.WaitAsync(); try { var (r, _) = await Tcp(hp.Item1, hp.Item2, 400); return (hp, r); } finally { sem.Release(); } }));
  var open = sibRes.Where(x => x.r == "OPEN").Select(x => $"{x.hp.Item1}:{x.hp.Item2}").ToArray();
  var alive = sibRes.Where(x => x.r == "ConnectionRefused").Select(x => x.hp.Item1).Distinct().ToArray();
  string myIp = ""; try { using var u = new UdpClient(); u.Connect("1.1.1.1", 80); myIp = ((IPEndPoint)u.Client.LocalEndPoint!).Address.ToString(); } catch (Exception e) { myIp = "n/a " + e.Message; }
  return new { at = DateTime.UtcNow, hostname = Dns.GetHostName(), my_ip = myIp, dns_github = dns, named = results, siblings = new { probed = sib.Count, open, alive_refused = alive } };
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
object? last = null;
_ = Task.Run(async () => { last = await Run(); Console.WriteLine("PROBE_RESULT " + JsonSerializer.Serialize(last)); });
app.MapGet("/", () => Results.Json(last ?? new { status = "probing, retry shortly" }, new JsonSerializerOptions { WriteIndented = true }));
app.MapGet("/rerun", async () => { last = await Run(); Console.WriteLine("PROBE_RESULT " + JsonSerializer.Serialize(last)); return Results.Json(last, new JsonSerializerOptions { WriteIndented = true }); });
app.MapGet("/healthz", () => Results.Text("ok"));
app.Run();
