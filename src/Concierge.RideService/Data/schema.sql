CREATE TABLE IF NOT EXISTS rides (
    id uuid PRIMARY KEY,
    idempotency_key text NOT NULL UNIQUE,
    guest_id uuid NOT NULL,
    stay_id uuid NOT NULL,
    checkout_date date NOT NULL,
    pickup_name text NOT NULL,
    pickup_latitude double precision NOT NULL,
    pickup_longitude double precision NOT NULL,
    dropoff_name text NOT NULL,
    dropoff_latitude double precision NOT NULL,
    dropoff_longitude double precision NOT NULL,
    is_immediate boolean NOT NULL,
    pickup_at_utc timestamptz NOT NULL,
    booked_at_utc timestamptz NOT NULL,
    route_duration_seconds double precision NOT NULL DEFAULT 1800,
    stay_workflow_id text NOT NULL DEFAULT '',
    status text NOT NULL CHECK (status IN ('Booked', 'DriverAssigned', 'PickedUp', 'Completed', 'Cancelled')),
    driver_name text,
    driver_car text,
    driver_plate text,
    picked_up_at_utc timestamptz,
    completed_at_utc timestamptz,
    cancelled_at_utc timestamptz,
    created_at_utc timestamptz NOT NULL
);

ALTER TABLE rides ADD COLUMN IF NOT EXISTS route_duration_seconds double precision NOT NULL DEFAULT 1800;
ALTER TABLE rides ADD COLUMN IF NOT EXISTS stay_workflow_id text NOT NULL DEFAULT '';
ALTER TABLE rides ADD COLUMN IF NOT EXISTS driver_name text;
ALTER TABLE rides ADD COLUMN IF NOT EXISTS driver_car text;
ALTER TABLE rides ADD COLUMN IF NOT EXISTS driver_plate text;
ALTER TABLE rides ADD COLUMN IF NOT EXISTS picked_up_at_utc timestamptz;
ALTER TABLE rides ADD COLUMN IF NOT EXISTS completed_at_utc timestamptz;
ALTER TABLE rides ADD COLUMN IF NOT EXISTS cancelled_at_utc timestamptz;

CREATE INDEX IF NOT EXISTS rides_guest_stay_pickup_idx
    ON rides (guest_id, stay_id, pickup_at_utc);

CREATE TABLE IF NOT EXISTS ride_events (
    ride_id uuid NOT NULL REFERENCES rides(id),
    guest_id uuid NOT NULL,
    sequence integer NOT NULL,
    status text NOT NULL,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at_utc timestamptz NOT NULL,
    PRIMARY KEY (ride_id, sequence)
);
