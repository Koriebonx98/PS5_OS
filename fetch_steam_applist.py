#!/usr/bin/env python3
"""
Fetch Steam app list and write CSV of AppId,Name. Keeps console open until Enter is pressed.

Usage:
  python fetch_steam_applist.py [-k API_KEY] [-o OUT_CSV] [--json]
"""

from __future__ import annotations
import argparse
import csv
import json
import sys
import time
from typing import Iterable, Tuple

try:
    import requests
except Exception:
    print("Please install requests: pip install requests", file=sys.stderr)
    sys.exit(1)

DEFAULT_API_KEY = "1B27234A8D97A72C97E53BC43D646C1F"
DEFAULT_URL = "https://api.steampowered.com/ISteamApps/GetAppList/v2/"


def fetch_applist(api_key: str, timeout: int = 60, max_attempts: int = 3, backoff: float = 2.0) -> dict:
    # Note: The GetAppList endpoint does not use an API key. Passing a key as a query
    # parameter can result in a 404 from the Steam server. We ignore the key here.
    if api_key:
        print("Note: GetAppList endpoint does not require an API key; the provided key will be ignored.", file=sys.stderr)

    headers = {
        'User-Agent': 'fetch_steam_applist/1.0',
        'Accept': 'application/json'
        # requests will handle Accept-Encoding automatically
    }

    attempt = 0
    while attempt < max_attempts:
        attempt += 1
        try:
            resp = requests.get(DEFAULT_URL, headers=headers, timeout=timeout)
            # If 404, surface a clearer error
            if resp.status_code == 404:
                resp.raise_for_status()
            resp.raise_for_status()
            return resp.json()
        except Exception as ex:
            print(f"Attempt {attempt} failed: {ex}", file=sys.stderr)
            if attempt >= max_attempts:
                raise
            time.sleep(backoff * attempt)


def extract_apps(json_data: dict) -> Iterable[Tuple[int, str]]:
    applist = json_data.get("applist") or {}
    apps = applist.get("apps") or []
    for e in apps:
        # Accept various key casings
        appid = e.get("appid") or e.get("AppId") or e.get("AppID")
        name = e.get("name") or e.get("Name") or ""
        try:
            yield int(appid), str(name).strip()
        except Exception:
            # skip invalid entries
            continue


def write_csv(path: str, apps: Iterable[Tuple[int, str]]) -> None:
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["AppId", "Name"])
        for appid, name in apps:
            w.writerow([appid, name])


def main() -> int:
    p = argparse.ArgumentParser(description="Fetch Steam app list and save AppId+Name to CSV.")
    p.add_argument("--api-key", "-k", default=DEFAULT_API_KEY, help="Steam API key")
    p.add_argument("--out", "-o", default="steam_apps.csv", help="Output CSV file")
    p.add_argument("--json", action="store_true", help="Also save raw JSON to steam_applist.json")
    args = p.parse_args()

    print("Fetching Steam app list...", file=sys.stderr)
    try:
        data = fetch_applist(args.api_key)
    except Exception as ex:
        print(f"Failed to fetch app list: {ex}", file=sys.stderr)
        input("Press Enter to exit...")
        return 2

    if args.json:
        try:
            with open("steam_applist.json", "w", encoding="utf-8") as jf:
                json.dump(data, jf, ensure_ascii=False, indent=2)
        except Exception as ex:
            print(f"Failed to save JSON: {ex}", file=sys.stderr)

    apps = list(extract_apps(data))
    print(f"Fetched {len(apps)} apps. Writing to {args.out} ...", file=sys.stderr)

    try:
        write_csv(args.out, apps)
    except Exception as ex:
        print(f"Failed to write CSV: {ex}", file=sys.stderr)
        input("Press Enter to exit...")
        return 3

    print("Done.", file=sys.stderr)
    try:
        input("Press Enter to exit...")
    except Exception:
        # In environments without stdin, ignore
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
