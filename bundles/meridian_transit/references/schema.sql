-- Schema and seed data for bundles/meridian_transit.
--
-- This is the shape `computations/daily-ridership.md` expects, and the data the
-- integration test asserts against. It is a reference resource, not a concept:
-- OKF loads only .md files as concepts (§3).
--
-- Apply it to a Postgres reachable from the SqlClient container. See running.md.

CREATE TABLE trips (
    trip_id      bigint PRIMARY KEY,
    rider_id     bigint  NOT NULL,
    service_date date    NOT NULL,
    status       text    NOT NULL CHECK (status IN ('completed', 'abandoned')),
    fare_cents   integer NOT NULL CHECK (fare_cents >= 0)
);

-- 2026-09-10: seven trips, five completed, three distinct riders among them.
-- Rider 1 makes four completed trips at 250 each -- enough to cross a 700 cap,
-- which is what makes this date useful for the capped-fare computation too.
INSERT INTO trips (trip_id, rider_id, service_date, status, fare_cents) VALUES
    (1, 1, '2026-09-10', 'completed', 250),
    (2, 1, '2026-09-10', 'completed', 250),
    (3, 1, '2026-09-10', 'completed', 250),
    (4, 1, '2026-09-10', 'completed', 250),
    (5, 2, '2026-09-10', 'completed', 250),
    (6, 3, '2026-09-10', 'abandoned', 250),
    (7, 3, '2026-09-10', 'abandoned', 250);

-- A neighbouring operating day, so a query that ignores service_date is visibly
-- wrong rather than accidentally right.
INSERT INTO trips (trip_id, rider_id, service_date, status, fare_cents) VALUES
    (8, 4, '2026-09-11', 'completed', 250),
    (9, 5, '2026-09-11', 'completed', 250);

-- Expected for service_date = 2026-09-10:
--   completed_trips = 5   (trips 1-5; the two abandoned ones do not count)
--   distinct_riders = 2   (riders 1 and 2; rider 3 only abandoned)
