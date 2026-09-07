# Network-isolation probe (python stack). TCP connect attempts only, short timeouts.
import socket, json, os, time, concurrent.futures as cf
from http.server import BaseHTTPRequestHandler, HTTPServer

NAMED = [
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
]

def tcp(host, port, timeout):
    t0 = time.time()
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return "OPEN", round((time.time()-t0)*1000)
    except socket.timeout:
        return "timeout", round((time.time()-t0)*1000)
    except OSError as e:
        return (e.strerror or str(e)).replace("[Errno", "").strip(), round((time.time()-t0)*1000)

def run():
    named = []
    for label, host, port in NAMED:
        r, ms = tcp(host, port, 2.5)
        named.append({"label": label, "result": r, "ms": ms})
    try: dns_github = socket.gethostbyname("github.com")
    except OSError as e: dns_github = str(e)
    sib = [(f"10.42.0.{h}", p) for h in range(10, 250) for p in (8080, 5000, 3000)]
    with cf.ThreadPoolExecutor(max_workers=64) as ex:
        res = list(ex.map(lambda hp: (hp, tcp(hp[0], hp[1], 0.4)[0]), sib))
    open_ = [f"{h}:{p}" for (h, p), r in res if r == "OPEN"]
    alive = sorted({h for (h, p), r in res if "refused" in r.lower()})
    my_ip = ""
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.connect(("1.1.1.1", 80)); my_ip = s.getsockname()[0]; s.close()
    except OSError as e: my_ip = f"n/a ({e})"
    return {"at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "hostname": socket.gethostname(), "my_ip": my_ip,
            "dns_github": dns_github, "named": named,
            "siblings": {"probed": len(sib), "open": open_, "alive_refused": alive}}

LAST = run()
print("PROBE_RESULT " + json.dumps(LAST), flush=True)

class H(BaseHTTPRequestHandler):
    def do_GET(self):
        global LAST
        if self.path.startswith("/rerun"):
            LAST = run(); print("PROBE_RESULT " + json.dumps(LAST), flush=True)
        body = json.dumps(LAST, indent=2).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *a): pass

print("Serving HTTP on 0.0.0.0 port 8080", flush=True)
HTTPServer(("0.0.0.0", 8080), H).serve_forever()
