BEGIN;

CREATE TABLE public.multiplayer_rooms
(
    room_id text PRIMARY KEY,
    host_user_id text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    status text NOT NULL CHECK (status IN ('WaitingForPlayers', 'Started')),
    maximum_human_players smallint NOT NULL CHECK (maximum_human_players BETWEEN 2 AND 4),
    bot_profiles jsonb NOT NULL CHECK
    (
        jsonb_typeof(bot_profiles) = 'object'
        AND bot_profiles ?& ARRAY['East', 'North', 'West']
        AND bot_profiles ->> 'East' IN ('random', 'beginner', 'skilled', 'expert', 'master')
        AND bot_profiles ->> 'North' IN ('random', 'beginner', 'skilled', 'expert', 'master')
        AND bot_profiles ->> 'West' IN ('random', 'beginner', 'skilled', 'expert', 'master')
    ),
    invitation_code_hash bytea NOT NULL CHECK (octet_length(invitation_code_hash) = 32)
);

CREATE TABLE public.multiplayer_room_seats
(
    room_id text NOT NULL REFERENCES public.multiplayer_rooms (room_id) ON DELETE CASCADE,
    seat text NOT NULL CHECK (seat IN ('South', 'East', 'North', 'West')),
    owner_user_id text,
    is_host boolean NOT NULL,
    is_ready boolean NOT NULL DEFAULT FALSE,
    bot_profile_id text CHECK (bot_profile_id IN ('random', 'beginner', 'skilled', 'expert', 'master')),
    joined_at timestamp with time zone,
    PRIMARY KEY (room_id, seat),
    CHECK (is_host = (seat = 'South')),
    CHECK (NOT (owner_user_id IS NOT NULL AND bot_profile_id IS NOT NULL)),
    CHECK
    (
        (owner_user_id IS NULL AND joined_at IS NULL AND NOT is_ready)
        OR (owner_user_id IS NOT NULL AND joined_at IS NOT NULL AND bot_profile_id IS NULL)
    )
);

CREATE UNIQUE INDEX multiplayer_room_seats_unique_owner
    ON public.multiplayer_room_seats (room_id, owner_user_id)
    WHERE owner_user_id IS NOT NULL;

CREATE UNIQUE INDEX multiplayer_rooms_unique_invitation_hash
    ON public.multiplayer_rooms (invitation_code_hash);

ALTER TABLE public.multiplayer_rooms ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.multiplayer_room_seats ENABLE ROW LEVEL SECURITY;

REVOKE ALL ON TABLE public.multiplayer_rooms FROM anon, authenticated;
REVOKE ALL ON TABLE public.multiplayer_room_seats FROM anon, authenticated;

COMMIT;
