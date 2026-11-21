"""Load or verify the fictional RoyaltyFlow portfolio through the HTTP API."""

import argparse
import csv
import hashlib
import io
import json
import sys
from datetime import date, datetime, timedelta, timezone
from uuid import UUID, uuid5

from smoke_test import Api, HEADER, check, ensure_record, verify_totals


CATALOG = {
    "Кино": ("Группа крови", "Звезда по имени Солнце", "Пачка сигарет"),
    "ДДТ": ("Что такое осень", "Родина", "Просвистела"),
    "Би-2": ("Полковнику никто не пишет", "Мой рок-н-ролл", "Серебро"),
    "Сплин": ("Выхода нет", "Орбит без сахара", "Моё сердце"),
    "Земфира": ("Искала", "Хочешь?", "Ариведерчи"),
    "Баста": ("Сансара", "Выпускной (Медлячок)", "Моя игра"),
    "Мот": ("Капкан", "Август — это ты", "Сопрано"),
    "Полина Гагарина": ("Кукушка", "Спектакль окончен", "Драмы больше нет"),
    "Дима Билан": ("На берегу неба", "Невозможное возможно", "Believe"),
    "Сергей Лазарев": ("You Are the Only One", "Так красиво", "Сдавайся"),
}
NAMESPACE = UUID("162f4913-9e4b-4a19-bb7c-368ad56ef8df")
CHANNELS = (
    ("[DEMO] Яндекс Музыка", "RU", 7),
    ("[DEMO] VK Музыка", "BY", 3),
    ("[DEMO] Spotify", "KZ", 4),
    ("[DEMO] Apple Music", "US", 2),
)


def stable_id(*parts):
    return str(uuid5(NAMESPACE, "/".join(parts)))


def portfolio_catalog():
    artists, tracks, contracts = [], [], []
    holders = [
        {"id": stable_id("holder", key), "name": f"[DEMO] Вымышленный правообладатель «{name}»"}
        for key, name in (("north", "Атлас звука"), ("south", "Маяк ритма"))
    ]
    for index, (name, songs) in enumerate(CATALOG.items()):
        artist_id = stable_id("artist", name)
        artists.append({"id": artist_id, "name": name})
        for rank, song in enumerate(songs):
            track_id = stable_id("track", name, song)
            tracks.append({"id": track_id, "artistId": artist_id, "title": f"{song} [DEMO]"})
            share = 60 if (index + rank) % 2 == 0 else 70
            contracts.append({
                "trackId": track_id, "validFrom": "2020-01-01", "validTo": None,
                "shares": [
                    {"rightsHolderId": holders[0]["id"], "percentage": share},
                    {"rightsHolderId": holders[1]["id"], "percentage": 100 - share},
                ],
            })
    return {
        "/api/artists": artists, "/api/rights-holders": holders,
        "/api/tracks": tracks, "/api/contracts": contracts,
    }


def month_start(reference, months_back):
    year, month = divmod(reference.year * 12 + reference.month - 1 - months_back, 12)
    return date(year, month + 1, 1)


def monthly_csv(month, tracks, expected):
    output = io.StringIO(newline="")
    writer = csv.writer(output, lineterminator="\n")
    writer.writerow(HEADER)
    for index, track in enumerate(tracks):
        track_id = track["id"]
        popularity = (11, 8, 5)[index % 3]
        for day in (3, 10, 17, 24):
            usage_date = month.replace(day=day).isoformat()
            for platform, country, weight in CHANNELS:
                event_id = stable_id("usage", track_id, usage_date, platform, country)
                variation = int.from_bytes(UUID(event_id).bytes[:4], "big") % 800
                plays = (150 + variation) * popularity * weight // 10
                writer.writerow((event_id, track_id, platform, country, usage_date, plays))
                expected[track_id][event_id] = (date.fromisoformat(usage_date), plays)
    return output.getvalue().encode("utf-8")


def catalog_matches(path, actual, expected):
    if path != "/api/contracts":
        return all(actual.get(field) == value for field, value in expected.items())
    return (
        all(actual.get(field) == expected[field] for field in ("trackId", "validFrom", "validTo"))
        and sorted(actual["shares"], key=lambda share: share["rightsHolderId"])
        == sorted(expected["shares"], key=lambda share: share["rightsHolderId"])
    )


def check_catalog(api, catalog, *, create):
    counts = {}
    for path, records in catalog.items():
        identity = "trackId" if path == "/api/contracts" else "id"
        existing = {}
        for row in api.pages(path):
            existing.setdefault(row[identity], []).append(row)
        count = 0
        for record in records:
            record_id = record[identity]
            matches = lambda row: catalog_matches(path, row, record)
            found = sum(matches(row) for row in existing.get(record_id, ()))
            if not found and create:
                check(record_id not in existing, f"{path}: conflicting portfolio record {record_id}.")
                if path == "/api/artists":
                    check(not any(row["name"] == record["name"] for rows in existing.values() for row in rows),
                          f"{path}: artist already exists with another ID ({record['name']}).")
                ensure_record(api, path, record, matches, {})
                found = 1
            check(found == 1, f"{path}: expected exactly one matching portfolio record {record_id}.")
            count += found
        counts[path] = count
    return counts


def seed(writer, reader, reference):
    writer.request("/api/artists?page=1&pageSize=1", (401,), authenticated=False)
    reader.request("/api/artists?page=1&pageSize=1")
    catalog = portfolio_catalog()
    check_catalog(writer, catalog, create=True)
    tracks = catalog["/api/tracks"]
    expected = {track["id"]: {} for track in tracks}
    for offset in range(6, 0, -1):
        csv_bytes = monthly_csv(month_start(reference, offset), tracks, expected)
        _, receipt = writer.request("/api/imports/upload", (200, 201), upload=csv_bytes)
        stats = receipt["statistics"]
        size = len(tracks) * 4 * len(CHANNELS)
        check(receipt["status"] == "Completed" and receipt["sha256"] == hashlib.sha256(csv_bytes).hexdigest()
              and stats["total"] == size and stats["invalid"] == 0
              and stats["inserted"] + stats["duplicate"] == size, "Unexpected portfolio import receipt.")

    counts = check_catalog(reader, catalog, create=False)
    period = {"from": month_start(reference, 6).isoformat(),
              "to": (month_start(reference, 0) - timedelta(days=1)).isoformat()}
    verify_totals(reader, expected, period)
    return counts, sum(map(len, expected.values())), period


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:18080")
    parser.add_argument("--verify-url", help="HTTPS origin for demo verification. Defaults to the write API.")
    args = parser.parse_args()
    try:
        writer = Api(args.base_url, read_only=False)
        reader = Api(args.verify_url or args.base_url, read_only=True)
        counts, events, period = seed(writer, reader, datetime.now(timezone.utc).date())
        print(f"Fictional portfolio verified ({period['from']} to {period['to']}): "
              f"{counts['/api/artists']} artists, {counts['/api/tracks']} tracks, "
              f"{counts['/api/rights-holders']} rights holders, {counts['/api/contracts']} contracts, "
              f"{events} events and exact royalties.")
    except (ValueError, OSError, KeyError, TypeError, ArithmeticError) as error:
        message = str(error) if type(error) is ValueError else type(error).__name__
        print(f"Portfolio seed failed: {message}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
