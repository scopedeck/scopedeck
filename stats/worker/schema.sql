-- One row per day and app build: how many update checks (≈ app starts) arrived.
-- No IP addresses or identifiers are stored.
CREATE TABLE IF NOT EXISTS checks (
  day     TEXT NOT NULL,   -- UTC, YYYY-MM-DD
  version TEXT NOT NULL,
  os      TEXT NOT NULL,   -- windows | macos | linux | other
  arch    TEXT NOT NULL,   -- x64 | arm64 | other
  kind    TEXT NOT NULL,   -- desktop | server
  country TEXT NOT NULL,   -- two-letter code from Cloudflare, XX if unknown
  n       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, version, os, arch, kind, country)
);
