import csv
import hashlib
import io
import unittest
from collections import defaultdict
from datetime import date
from decimal import Decimal

from seed_portfolio import CHANNELS, month_start, monthly_csv, portfolio_catalog, seed
from smoke_test import ARTIST_ID, check


class MemoryApi:
    def __init__(self, *, read_only=False, state=None):
        self.read_only = read_only
        self.catalog, self.receipts, self.usage = state or (
            defaultdict(list), {}, {}
        )
        self.auth_active = True
        self.catalog_writes = 0

    def request(self, path, statuses=(200,), *, authenticated=True, payload=None, upload=None):
        if path == "/api/artists?page=1&pageSize=1":
            status = 200 if authenticated or not self.auth_active else 401
            check(status in statuses, "Unexpected authentication status.")
            return status, None
        check(authenticated and not self.read_only, "Portfolio writes require the operator.")
        if upload is None:
            self.catalog_writes += 1
            if path == "/api/contracts":
                payload = {**payload, "shares": list(reversed(payload["shares"]))}
            self.catalog[path].append(payload)
            return 201, payload

        digest = hashlib.sha256(upload).hexdigest()
        if digest in self.receipts:
            return 200, self.receipts[digest]
        rows = list(csv.DictReader(io.StringIO(upload.decode("utf-8"))))
        new_events = 0
        for row in rows:
            normalized = {
                "eventId": row["EventId"], "trackId": row["TrackId"],
                "usageDate": row["UsageDate"], "playCount": int(row["PlayCount"]),
            }
            new_events += row["EventId"] not in self.usage
            self.usage.setdefault(row["EventId"], normalized)
        receipt = {"status": "Completed", "sha256": digest,
                   "statistics": {"total": len(rows), "inserted": new_events,
                                  "duplicate": len(rows) - new_events, "invalid": 0}}
        self.receipts[digest] = receipt
        return 201, receipt

    def pages(self, path, **filters):
        if path in portfolio_catalog():
            yield from self.catalog[path]
            return
        rows = [row for row in self.usage.values() if filters["from"] <= row["usageDate"] <= filters["to"]]
        if path == "/api/usage":
            yield from (row for row in rows if row["trackId"] == filters["trackId"])
        elif path == "/api/royalties/summary":
            by_track = defaultdict(list)
            for row in rows:
                by_track[row["trackId"]].append(row["playCount"])
            for track, plays in by_track.items():
                # Reference half-up rounding of 1.5 cents per play, on each event.
                cents = sum((3 * count + 1) // 2 for count in plays)
                yield {"trackId": track, "events": len(plays), "playCount": sum(plays),
                       "royaltyAmount": Decimal(cents) / 100}
        else:
            raise AssertionError(f"Unexpected path: {path}")


class PortfolioTests(unittest.TestCase):
    def setUp(self):
        self.writer = MemoryApi()
        self.reader = MemoryApi(read_only=True,
                                state=(self.writer.catalog, self.writer.receipts, self.writer.usage))

    def test_seed_rerun_preserves_smoke_and_reuses_complete_months(self):
        smoke_artist = {"id": ARTIST_ID, "name": "RoyaltyFlow smoke artist"}
        self.writer.catalog["/api/artists"].append(smoke_artist)
        reference = date(2026, 11, 5)
        counts, events, period = seed(self.writer, self.reader, reference)
        self.assertEqual(counts, {"/api/artists": 10, "/api/rights-holders": 2,
                                  "/api/tracks": 30, "/api/contracts": 30})
        self.assertEqual(events, 2880)
        self.assertEqual(period, {"from": "2026-05-01", "to": "2026-10-31"})
        self.assertIn(smoke_artist, self.writer.catalog["/api/artists"])
        self.assertEqual(self.writer.catalog_writes, 72)

        self.assertEqual(seed(self.writer, self.reader, reference.replace(day=30)), (counts, events, period))
        self.assertEqual(len(self.writer.usage), 2880)
        self.assertEqual(len(self.writer.receipts), 6)
        self.assertEqual(self.writer.catalog_writes, 72)

        seed(self.writer, self.reader, date(2026, 12, 1))
        self.assertEqual(len(self.writer.usage), 3360)
        self.assertEqual(len(self.writer.receipts), 7)
        self.assertEqual(self.writer.catalog_writes, 72)

    def test_month_coverage_markers_and_split(self):
        catalog = portfolio_catalog()
        tracks = catalog["/api/tracks"]
        self.assertTrue(all(track["title"].endswith(" [DEMO]") for track in tracks))
        splits = {tuple(sorted(share["percentage"] for share in row["shares"]))
                  for row in catalog["/api/contracts"]}
        self.assertEqual(splits, {(30, 70), (40, 60)})
        month = month_start(date(2027, 1, 1), 6)
        self.assertEqual(month, date(2026, 7, 1))
        expected = {track["id"]: {} for track in tracks}
        rows = list(csv.DictReader(io.StringIO(monthly_csv(month, tracks, expected).decode())))
        self.assertEqual(len({row["EventId"] for row in rows}), 480)
        self.assertEqual({(r["Platform"], r["Country"]) for r in rows},
                         {(platform, country) for platform, country, _ in CHANNELS})
        self.assertGreater(len({r["PlayCount"] for r in rows}), 100)

    def test_rejects_legacy_unprotected_api_before_writing(self):
        self.writer.auth_active = False
        with self.assertRaisesRegex(ValueError, "authentication"):
            seed(self.writer, self.reader, date(2026, 11, 1))
        self.assertEqual(self.writer.catalog_writes, 0)
        self.assertEqual(self.writer.receipts, {})

    def test_rejects_existing_conflicting_seed_contract_without_deleting_it(self):
        day = date(2026, 11, 1)
        seed(self.writer, self.reader, day)
        contracts = self.writer.catalog["/api/contracts"]
        contracts[0]["shares"][0]["percentage"] = 10
        with self.assertRaisesRegex(ValueError, "conflicting portfolio record"):
            seed(self.writer, self.reader, day)
        self.assertEqual(contracts[0]["shares"][0]["percentage"], 10)
        self.assertEqual(len(contracts), 30)
        self.assertEqual(len(self.writer.receipts), 6)


if __name__ == "__main__":
    unittest.main()
