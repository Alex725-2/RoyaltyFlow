"""Exercise RoyaltyFlow with the unmodified, deterministic sample CSV."""

import argparse
import csv
import hashlib
import io
import json
import sys
from collections import defaultdict
from datetime import date, timedelta
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen
from uuid import UUID


ARTIST_ID = "6677f002-c9c8-42b1-9c7a-4c2c032cb34c"
HOLDER_ID = "3ac40a29-b0e1-45b5-8608-45d65a9bf184"
HEADER = ["EventId", "TrackId", "Platform", "Country", "UsageDate", "PlayCount"]
SAMPLE = Path(__file__).resolve().parent.parent / "data" / "sample-small.csv"
RATE = Decimal("0.015")


def check(condition, message):
    if not condition:
        raise ValueError(message)


class Api:
    def __init__(self, base_url):
        parsed = urlsplit(base_url)
        check(parsed.scheme in {"http", "https"} and parsed.hostname and
              not parsed.username and not parsed.password and not parsed.query and
              not parsed.fragment, "Provide an HTTP(S) base URL without credentials or query parameters.")
        self.base_url = base_url.rstrip("/")

    def request(self, path, statuses=(200,), *, payload=None, upload=None):
        method = "POST" if payload is not None or upload is not None else "GET"
        body = upload if upload is not None else json.dumps(payload).encode() if payload is not None else None
        headers = {}
        if body is not None:
            headers["Content-Type"] = "text/csv" if upload is not None else "application/json"
        request = Request(self.base_url + path, data=body, headers=headers, method=method)
        try:
            with urlopen(request, timeout=30) as response:
                status = response.status
                data = json.load(response, parse_float=Decimal)
        except HTTPError as error:
            status = error.code
            error.close()
            data = None
        except (URLError, TimeoutError) as error:
            raise ValueError(f"{method} {path}: connection failed or timed out.") from error
        check(status in statuses, f"{method} {path}: received HTTP {status}, expected {statuses}.")
        return status, data

    def pages(self, path, **filters):
        page = 1
        while True:
            query = urlencode(dict(filters, page=page, pageSize=200))
            _, data = self.request(f"{path}?{query}")
            yield from data["items"]
            if not data["hasNext"]:
                return
            page += 1


def load_sample():
    content = SAMPLE.read_bytes()
    reader = csv.DictReader(io.StringIO(content.decode("utf-8-sig"), newline=""))
    check(reader.fieldnames == HEADER, "Unexpected sample CSV header.")
    tracks = defaultdict(dict)
    events = set()
    for row in reader:
        event_id = str(UUID(row["EventId"]))
        track_id = str(UUID(row["TrackId"]))
        check(event_id not in events, f"Duplicate EventId in fixture: {event_id}.")
        events.add(event_id)
        playback = int(row["PlayCount"])
        check(playback > 0, "Fixture must have positive play counts to detect duplicated accruals.")
        tracks[track_id][event_id] = (date.fromisoformat(row["UsageDate"]), playback)
    check(len(events) == 100, "Expected 100 sample events.")
    return content, tracks


def ensure_record(api, path, payload, matches, conflict_records):
    status, response = api.request(path, (201, 409), payload=payload)
    if status == 201:
        check(matches(response), f"{path}: created record differs from the smoke fixture.")
    else:
        if path not in conflict_records:
            conflict_records[path] = list(api.pages(path))
        check(any(matches(row) for row in conflict_records[path]),
              f"{path}: existing record differs from the smoke fixture.")


def prepare_catalog(api, tracks, first_date, last_date):
    seen = {}
    artist = {"id": ARTIST_ID, "name": "RoyaltyFlow smoke artist"}
    holder = {"id": HOLDER_ID, "name": "RoyaltyFlow smoke rights holder"}
    for path, payload in [("/api/artists", artist), ("/api/rights-holders", holder)]:
        ensure_record(api, path, payload, lambda row: row == payload, seen)
    for track_id in sorted(tracks):
        track = {"id": track_id, "artistId": ARTIST_ID, "title": f"RoyaltyFlow smoke {track_id}"}
        ensure_record(api, "/api/tracks", track, lambda row: row == track, seen)
        contract = {
            "trackId": track_id,
            "validFrom": first_date.isoformat(),
            "validTo": (last_date + timedelta(days=1)).isoformat(),
            "shares": [{"rightsHolderId": HOLDER_ID, "percentage": 100}],
        }
        ensure_record(api, "/api/contracts", contract,
                      lambda row: all(row.get(key) == value for key, value in contract.items()), seen)


def verify_totals(api, tracks, period):
    summary = {}
    for row in api.pages("/api/royalties/summary", **period):
        track_id = row["trackId"]
        if track_id in tracks:
            check(track_id not in summary, f"Repeated analytics row for track {track_id}.")
            summary[track_id] = row

    for track_id, expected in tracks.items():
        usage = list(api.pages("/api/usage", trackId=track_id, **period))
        actual = {row["eventId"]: (date.fromisoformat(row["usageDate"]), row["playCount"]) for row in usage}
        check(len(usage) == len(expected) and actual == expected, f"Unexpected usage for track {track_id}.")
        expected_amount = sum((plays * RATE).quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)
                              for _, plays in expected.values())
        row = summary.get(track_id)
        check(row is not None and row["events"] == len(expected) and
              row["playCount"] == sum(plays for _, plays in expected.values()) and
              row["royaltyAmount"] == expected_amount,
              f"Missing, repeated or incorrect accruals for track {track_id}.")


def run(api):
    _, live = api.request("/health/live")
    _, ready = api.request("/health/ready")
    check(live["status"] == "Healthy" and ready["status"] == "Ready", "Unexpected health response.")
    content, tracks = load_sample()
    dates = [day for events in tracks.values() for day, _ in events.values()]
    first_date, last_date = min(dates), max(dates)
    prepare_catalog(api, tracks, first_date, last_date)

    expected = {"total": 100, "inserted": 100, "duplicate": 0, "invalid": 0}
    period = {"from": first_date.isoformat(), "to": last_date.isoformat()}
    _, first = api.request("/api/imports/upload", (200, 201), upload=content)
    check(first["status"] == "Completed" and first["statistics"] == expected and
          first["sha256"] == hashlib.sha256(content).hexdigest() and first["durationMs"] >= 0,
          "Unexpected first import status, SHA-256, duration or statistics.")
    _, repeated = api.request("/api/imports/upload", upload=content)
    _, persisted = api.request(f"/api/imports/{UUID(first['id'])}")
    check(first == repeated == persisted, "An identical re-upload must return the original persisted receipt.")
    verify_totals(api, tracks, period)
    print(f"Smoke OK: 100 distinct events, {len(tracks)} tracks, stable import receipt and exact royalties.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base_url", nargs="?", default="http://127.0.0.1:18080")
    parser.add_argument("--check-fixture", action="store_true", help="Check the CSV without HTTP or writes.")
    args = parser.parse_args()
    try:
        if args.check_fixture:
            _, tracks = load_sample()
            print(f"Fixture OK: 100 distinct events across {len(tracks)} tracks.")
        else:
            run(Api(args.base_url))
        return 0
    except (ValueError, OSError, KeyError, TypeError, ArithmeticError) as error:
        if isinstance(error, ValueError) and not isinstance(error, (json.JSONDecodeError, UnicodeError)):
            print(f"Smoke failed: {error}", file=sys.stderr)
        else:
            print(f"Smoke failed: {type(error).__name__}; check the fixture and API responses.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
