-- Creates a new empty caller-state table for backlog promotion notices; no existing owner table is altered.
-- The foreign key takes a SHARE ROW EXCLUSIVE lock on backlog_plan_promotions until commit.
CREATE TABLE IF NOT EXISTS {schema}.user_notification_promotion_dismissals (
    tenant_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    promotion_id uuid NOT NULL REFERENCES {schema}.backlog_plan_promotions(promotion_id),
    dismissed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, actor_issuer, actor_subject, promotion_id)
);
