CREATE TABLE IF NOT EXISTS huggingface_download_snapshots (
    model_id TEXT NOT NULL,
    snapshot_date DATE NOT NULL,
    collected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    downloads_30d BIGINT NOT NULL CHECK (downloads_30d >= 0),
    downloads_all_time BIGINT NOT NULL CHECK (downloads_all_time >= 0),
    likes BIGINT NOT NULL DEFAULT 0 CHECK (likes >= 0),
    last_modified TIMESTAMPTZ NULL,
    model_url TEXT NOT NULL,
    PRIMARY KEY (model_id, snapshot_date)
);

CREATE INDEX IF NOT EXISTS ix_huggingface_download_snapshots_collected_at
    ON huggingface_download_snapshots (collected_at DESC);

COMMENT ON TABLE huggingface_download_snapshots IS
    'Daily snapshots of Hugging Face model download counts collected by GitHub Actions.';

COMMENT ON COLUMN huggingface_download_snapshots.downloads_30d IS
    'The Hugging Face downloads value, representing downloads over the last 30 days.';

COMMENT ON COLUMN huggingface_download_snapshots.downloads_all_time IS
    'The cumulative downloadsAllTime value returned by the Hugging Face model API.';
