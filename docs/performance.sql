-- Run against a disposable database with representative sample data and current statistics.
-- These statements execute read queries. Replace example values before measuring.
EXPLAIN (ANALYZE, BUFFERS)
SELECT event_id, track_id, platform, country, usage_date, play_count
FROM usage_records
WHERE track_id = '00000000-0001-0000-0200-000000000000'::uuid
  AND usage_date BETWEEN DATE '2025-01-01' AND DATE '2025-01-31'
ORDER BY usage_date DESC, event_id DESC LIMIT 51;

EXPLAIN (ANALYZE, BUFFERS)
SELECT event_id, usage_date FROM usage_records
WHERE usage_date BETWEEN DATE '2025-01-01' AND DATE '2025-01-31'
ORDER BY usage_date DESC, event_id DESC LIMIT 51;

EXPLAIN (ANALYZE, BUFFERS)
WITH period AS MATERIALIZED (
    SELECT event_id, track_id, play_count FROM usage_records
    WHERE usage_date BETWEEN DATE '2025-01-01' AND DATE '2025-01-31'
),
usage_by_track AS (
    SELECT track_id, count(*) AS events, sum(play_count) AS plays
    FROM period GROUP BY track_id ORDER BY track_id LIMIT 51 OFFSET 0
),
accruals_by_track AS (
    SELECT p.track_id, sum(a.amount) AS amount
    FROM usage_by_track b JOIN period p ON p.track_id = b.track_id
    JOIN royalty_accruals a ON a.event_id = p.event_id GROUP BY p.track_id
)
SELECT b.track_id, t.title, b.events, b.plays, coalesce(a.amount, 0::numeric)
FROM usage_by_track b JOIN tracks t ON t.id = b.track_id
LEFT JOIN accruals_by_track a ON a.track_id = b.track_id ORDER BY b.track_id;

EXPLAIN (ANALYZE, BUFFERS)
SELECT u.event_id, c.id
FROM usage_records u JOIN royalty_contracts c
  ON c.track_id = u.track_id AND daterange(c.valid_from, c.valid_to, '[)') @> u.usage_date
WHERE u.import_id = '00000000-0000-0000-0000-000000000001'::uuid;
