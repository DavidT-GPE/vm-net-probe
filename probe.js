// Network-isolation probe. Read-only: TCP connect attempts only, short timeouts.
const net = require('net'), dns = require('dns'), http = require('http'), os = require('os');

const NAMED = [
  ['host bridge gw  10.42.0.1:5830 (daemon)', '10.42.0.1', 5830],
  ['host bridge gw  10.42.0.1:22 (ssh)', '10.42.0.1', 22],
  ['host LAN ip     172.24.18.62:5830 (daemon)', '172.24.18.62', 5830],
  ['host LAN ip     172.24.18.62:22 (ssh)', '172.24.18.62', 22],
  ['portal          172.24.18.200:5800', '172.24.18.200', 5800],
  ['portal          172.24.18.200:445 (smb)', '172.24.18.200', 445],
  ['edge/sql        172.24.18.206:443', '172.24.18.206', 443],
  ['edge/sql        172.24.18.206:1433', '172.24.18.206', 1433],
  ['router          172.24.18.1:443', '172.24.18.1', 443],
  ['dc              172.24.18.204:389 (ldap)', '172.24.18.204', 389],
  ['sydney          172.24.28.11:443', '172.24.28.11', 443],
  ['internet        1.1.1.1:443', '1.1.1.1', 443],
  ['internet        github.com:443', 'github.com', 443],
];

function tcp(host, port, ms) {
  return new Promise(res => {
    const start = Date.now(); const s = new net.Socket(); let done = false;
    const fin = (r) => { if (done) return; done = true; s.destroy(); res({ result: r, ms: Date.now() - start }); };
    s.setTimeout(ms);
    s.once('connect', () => fin('OPEN'));
    s.once('timeout', () => fin('timeout'));
    s.once('error', e => fin(e.code || String(e)));
    try { s.connect(port, host); } catch (e) { fin(e.code || String(e)); }
  });
}
async function pool(items, n, fn) { const out = []; let i = 0; await Promise.all(Array.from({ length: n }, async () => { while (i < items.length) { const k = i++; out[k] = await fn(items[k]); } })); return out; }

async function run() {
  const named = [];
  for (const [label, host, port] of NAMED) named.push({ label, host, port, ...(await tcp(host, port, 2500)) });
  const dnsRes = await new Promise(res => dns.lookup('github.com', (e, a) => res(e ? (e.code || String(e)) : a)));
  // sibling VMs on the bridge: 10.42.0.10-249, common workload ports
  const sib = [];
  for (let h = 10; h <= 249; h++) for (const p of [8080, 5000, 3000]) sib.push([`10.42.0.${h}`, p]);
  const sibRes = await pool(sib, 60, async ([host, port]) => ({ host, port, ...(await tcp(host, port, 400)) }));
  const siblingsOpen = sibRes.filter(r => r.result === 'OPEN').map(r => `${r.host}:${r.port}`);
  const siblingsRefused = sibRes.filter(r => r.result === 'ECONNREFUSED').map(r => r.host).filter((v, i, a) => a.indexOf(v) === i);
  const ifaces = Object.entries(os.networkInterfaces()).flatMap(([n, l]) => l.filter(x => x.family === 'IPv4').map(x => `${n}=${x.address}`));
  return { at: new Date().toISOString(), hostname: os.hostname(), ifaces, dns_github: dnsRes, named, siblings: { probed: sib.length, open: siblingsOpen, refused_hosts_alive: siblingsRefused } };
}

let last = null;
run().then(r => { last = r; console.log('PROBE_RESULT ' + JSON.stringify(r)); });
http.createServer(async (req, res) => {
  if (req.url === '/rerun') last = await run();
  res.setHeader('content-type', 'application/json');
  res.end(JSON.stringify(last || { status: 'probing, retry shortly' }, null, 2));
}).listen(3000, '0.0.0.0', () => console.log('probe listening on 3000'));
