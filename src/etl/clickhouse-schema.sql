-- ClickHouse schema for Ethereum blockchain analytics
-- Schema matches cryo (Rust) Parquet output with --hex flag
-- Applied to: clickhouse-ethereum-analytics service in ethereum namespace
-- Engine: ReplacingMergeTree (deduplicates on ORDER BY key)

-- Drop old mismatched tables
DROP TABLE IF EXISTS blocks;
DROP TABLE IF EXISTS transactions;
DROP TABLE IF EXISTS logs;

CREATE TABLE IF NOT EXISTS blocks (
    block_number UInt64,
    block_hash String,
    timestamp UInt32,
    author String,
    gas_used UInt64,
    extra_data String,
    base_fee_per_gas Nullable(UInt64),
    chain_id UInt64
) ENGINE = ReplacingMergeTree()
ORDER BY block_number;

CREATE TABLE IF NOT EXISTS transactions (
    block_number UInt64,
    transaction_index UInt64,
    transaction_hash String,
    nonce UInt64,
    from_address String,
    to_address Nullable(String),
    value_binary Nullable(String),
    value_string Nullable(String),
    value_f64 Nullable(Float64),
    input String,
    gas_limit UInt64,
    gas_used UInt64,
    gas_price Nullable(UInt64),
    transaction_type Nullable(UInt32),
    max_priority_fee_per_gas Nullable(UInt64),
    max_fee_per_gas Nullable(UInt64),
    success UInt8,
    n_input_bytes UInt32,
    n_input_zero_bytes UInt32,
    n_input_nonzero_bytes UInt32,
    chain_id UInt64
) ENGINE = ReplacingMergeTree()
ORDER BY (block_number, transaction_index);

CREATE TABLE IF NOT EXISTS logs (
    block_number UInt64,
    transaction_index UInt32,
    log_index UInt32,
    transaction_hash String,
    address String,
    topic0 Nullable(String),
    topic1 Nullable(String),
    topic2 Nullable(String),
    topic3 Nullable(String),
    data String,
    n_data_bytes UInt32,
    chain_id UInt64
) ENGINE = ReplacingMergeTree()
ORDER BY (block_number, log_index);

CREATE TABLE IF NOT EXISTS etl_progress (
    dataset String,
    last_block UInt64,
    updated_at DateTime
) ENGINE = ReplacingMergeTree()
ORDER BY dataset;
