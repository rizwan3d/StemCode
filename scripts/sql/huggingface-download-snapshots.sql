CREATE TABLE IF NOT EXISTS huggingface_download_snapshots (
    model_id TEXT NOT NULL,
    snapshot_date DATE NOT NULL,
    collected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    downloads BIGINT NOT NULL CHECK (downloads >= 0),
    likes BIGINT NOT NULL DEFAULT 0 CHECK (likes >= 0),
    last_modified TIMESTAMPTZ NULL,
    model_url TEXT NOT NULL,
    PRIMARY KEY (model_id, snapshot_date)
);

CREATE INDEX IF NOT EXISTS ix_huggingface_download_snapshots_collected_at
    ON huggingface_download_snapshots (collected_at DESC);

COMMENT ON TABLE huggingface_download_snapshots IS
    'Daily snapshots of Hugging Face model download counts collected by GitHub Actions.';

COMMENT ON COLUMN huggingface_download_snapshots.downloads IS
    'The downloads value returned by the Hugging Face model API at collection time.';
