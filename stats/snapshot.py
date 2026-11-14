"""Daily usage snapshot for ScopeDeck, run by .github/workflows/stats.yml.

Writes CSV files into the directory given as the first argument (the `stats` branch):
  downloads.csv  date,tag,asset,downloads      cumulative download count of every release file, per day
  views.csv      day,count,uniques             GitHub repo page views per day   (needs TRAFFIC_TOKEN)
  clones.csv     day,count,uniques             git clones per day                (needs TRAFFIC_TOKEN)
  referrers.csv  date,referrer,count,uniques   top referrers over the last 14 days, as seen on `date`
  paths.csv      date,path,count,uniques       top repo pages over the last 14 days, as seen on `date`
  checks.csv     day,version,os,arch,kind,country,n   update checks (≈ app starts) counted by the relay
                                               (needs CLOUDFLARE_API_TOKEN, CLOUDFLARE_ACCOUNT_ID, D1_DATABASE_ID)
GitHub keeps traffic for 14 days only, so daily rows are merged: newer values replace older ones for the same day.
"""
import csv, datetime, json, os, sys, urllib.request

REPO = "scopedeck/scopedeck"
OUT = sys.argv[1]
TODAY = datetime.datetime.now(datetime.timezone.utc).date().isoformat()


def get(url, token, method="GET", body=None):
    req = urllib.request.Request(url, method=method, data=None if body is None else json.dumps(body).encode())
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("User-Agent", "scopedeck-stats")
    req.add_header("Accept", "application/json")
    if body is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


def merge(name, header, rows, key_len):
    """Writes rows into name.csv; rows whose first key_len columns already exist replace the old ones."""
    path = os.path.join(OUT, name)
    data = {}
    if os.path.exists(path):
        with open(path, newline="", encoding="utf-8") as f:
            for row in list(csv.reader(f))[1:]:
                data[tuple(row[:key_len])] = row
    for row in rows:
        row = [str(v) for v in row]
        data[tuple(row[:key_len])] = row
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(header)
        w.writerows(sorted(data.values()))
    print(f"{name}: {len(rows)} rows merged, {len(data)} total")


gh = os.environ["GITHUB_TOKEN"]
releases, page = [], 1
while batch := get(f"https://api.github.com/repos/{REPO}/releases?per_page=100&page={page}", gh):
    releases += batch
    page += 1
merge("downloads.csv", ["date", "tag", "asset", "downloads"],
      [[TODAY, r["tag_name"], a["name"], a["download_count"]] for r in releases for a in r["assets"]], 3)

if traffic := os.environ.get("TRAFFIC_TOKEN"):
    base = f"https://api.github.com/repos/{REPO}/traffic"
    for kind in ("views", "clones"):
        items = get(f"{base}/{kind}", traffic)[kind]
        merge(f"{kind}.csv", ["day", "count", "uniques"],
              [[i["timestamp"][:10], i["count"], i["uniques"]] for i in items], 1)
    merge("referrers.csv", ["date", "referrer", "count", "uniques"],
          [[TODAY, i["referrer"], i["count"], i["uniques"]] for i in get(f"{base}/popular/referrers", traffic)], 2)
    merge("paths.csv", ["date", "path", "count", "uniques"],
          [[TODAY, i["path"], i["count"], i["uniques"]] for i in get(f"{base}/popular/paths", traffic)], 2)
else:
    print("TRAFFIC_TOKEN not set: skipping views, clones, referrers, paths")

cf, account, db = (os.environ.get(k) for k in ("CLOUDFLARE_API_TOKEN", "CLOUDFLARE_ACCOUNT_ID", "D1_DATABASE_ID"))
if cf and account and db:
    since = (datetime.date.fromisoformat(TODAY) - datetime.timedelta(days=14)).isoformat()
    result = get(f"https://api.cloudflare.com/client/v4/accounts/{account}/d1/database/{db}/query", cf, "POST",
                 {"sql": "SELECT day, version, os, arch, kind, country, n FROM checks WHERE day >= ?", "params": [since]})
    rows = result["result"][0]["results"]
    merge("checks.csv", ["day", "version", "os", "arch", "kind", "country", "n"],
          [[r["day"], r["version"], r["os"], r["arch"], r["kind"], r["country"], r["n"]] for r in rows], 6)
else:
    print("Cloudflare secrets not set: skipping update-check counts")
