BEGIN;

ALTER TABLE public.multiplayer_rooms
    DROP CONSTRAINT multiplayer_rooms_status_check;

ALTER TABLE public.multiplayer_rooms
    ADD CONSTRAINT multiplayer_rooms_status_check
        CHECK (status IN ('WaitingForPlayers', 'Starting', 'Started'));

COMMIT;
